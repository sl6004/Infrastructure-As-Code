﻿using InfrastructureAsCode.Core.Models;
using Microsoft.SharePoint.Client;
using Microsoft.SharePoint.Client.WebParts;
using OfficeDevPnP.Core.Entities;
using OfficeDevPnP.Core.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;

namespace InfrastructureAsCode.Core.Extensions
{
    public static class WebExtensions
    {
        public static Web GetWebById(this Web currentWeb, Guid guid)
        {
            var clientContext = currentWeb.Context as ClientContext;
            Site site = clientContext.Site;
            Web web = site.OpenWebById(guid);
            clientContext.Load(web, w => w.Url, w => w.Title, w => w.Id);
            clientContext.ExecuteQueryRetry();

            return web;
        }

        public static Web GetWebByUrl(this Web currentWeb, string url)
        {
            var clientContext = currentWeb.Context as ClientContext;
            Site site = clientContext.Site;
            Web web = site.OpenWeb(url);
            clientContext.Load(web, w => w.Url, w => w.Title, w => w.Id);
            clientContext.ExecuteQueryRetry();

            return web;
        }

        /// <summary>
        /// Will scan the web site groups for groups to provision and retrieve
        /// </summary>
        /// <param name="hostWeb">The host web to which the groups will be created</param>
        /// <param name="groupsToProvision">The collection of groups to retrieve and/or provision</param>
        /// <returns></returns>
        public static IList<SPGroupDefinitionModel> GetOrCreateSiteGroups(this Web hostWeb, List<SPGroupDefinitionModel> groupsToProvision)
        {
            var siteGroups = new List<SPGroupDefinitionModel>();
            var context = hostWeb.Context;

            var groups = hostWeb.SiteGroups;
            context.Load(groups, g => g.Include(inc => inc.Id, inc => inc.Title));
            context.ExecuteQuery();

            foreach (var groupDef in groupsToProvision)
            {
                if (groups.Any(g => g.Title.Equals(groupDef.Title, StringComparison.CurrentCultureIgnoreCase)))
                {
                    var group = groups.FirstOrDefault(g => g.Title.Equals(groupDef.Title, StringComparison.CurrentCultureIgnoreCase));
                    siteGroups.Add(new SPGroupDefinitionModel() { Id = group.Id, Title = group.Title });
                    continue;
                }

                // Create Group
                var groupCreationInfo = new GroupCreationInformation();
                groupCreationInfo.Title = groupDef.Title;
                groupCreationInfo.Description = groupDef.Description;

                var oGroup = hostWeb.SiteGroups.Add(groupCreationInfo);
                context.Load(oGroup);
                oGroup.Owner = hostWeb.CurrentUser;
                oGroup.OnlyAllowMembersViewMembership = groupDef.OnlyAllowMembersViewMembership;
                oGroup.AllowMembersEditMembership = groupDef.AllowMembersEditMembership;
                oGroup.AllowRequestToJoinLeave = groupDef.AllowRequestToJoinLeave;
                oGroup.AutoAcceptRequestToJoinLeave = groupDef.AutoAcceptRequestToJoinLeave;
                oGroup.Update();
                context.Load(oGroup, g => g.Id, g => g.Title);
                context.ExecuteQuery();
                siteGroups.Add(new SPGroupDefinitionModel() { Id = oGroup.Id, Title = oGroup.Title });
            }

            return siteGroups;
        }

        /// <summary>
        /// Provisions a site column based on the field definition specified
        /// </summary>
        /// <param name="hostWeb">The instantiated site/web to which the column will be retrieved and/or provisioned</param>
        /// <param name="fieldDef">The definition for the field</param>
        /// <param name="loggerVerbose">Provides a method for verbose logging</param>
        /// <param name="loggerError">Provides a method for exception logging</param>
        /// <param name="SiteGroups">(OPTIONAL) collection of group, required if this is a PeoplePicker field</param>
        /// <param name="JsonFilePath">(OPTIONAL) file path except if loading choices from JSON</param>
        /// <returns></returns>
        public static Field CreateColumn(this Web hostWeb, SPFieldDefinitionModel fieldDef, Action<string, string[]> loggerVerbose, Action<Exception, string, string[]> loggerError, List<SPGroupDefinitionModel> SiteGroups, string JsonFilePath = null)
        {
            if (!hostWeb.IsPropertyAvailable("Context"))
            {

            }

            var fields = hostWeb.Fields;
            hostWeb.Context.Load(fields, fc => fc.Include(f => f.Id, f => f.InternalName, f => f.Title, f => f.JSLink, f => f.Indexed, f => f.CanBeDeleted, f => f.Required));
            hostWeb.Context.ExecuteQueryRetry();


            var returnField = fields.FirstOrDefault(f => f.Id == fieldDef.FieldGuid || f.InternalName == fieldDef.InternalName);
            if (returnField == null)
            {
                var finfoXml = fieldDef.CreateFieldDefinition(SiteGroups, JsonFilePath);
                loggerVerbose("Provision field {0} with XML:{1}", new string[] { fieldDef.InternalName, finfoXml });
                try
                {
                    var createdField = hostWeb.CreateField(finfoXml, executeQuery: false);
                    hostWeb.Context.ExecuteQueryRetry();
                }
                catch (Exception ex)
                {
                    loggerError(ex, "EXCEPTION: field {0} with message {1}", new string[] { fieldDef.InternalName, ex.Message });
                }
                finally
                {
                    returnField = hostWeb.Fields.GetByInternalNameOrTitle(fieldDef.InternalName);
                    hostWeb.Context.Load(returnField, fd => fd.Id, fd => fd.Title, fd => fd.Indexed, fd => fd.InternalName, fd => fd.CanBeDeleted, fd => fd.Required);
                    hostWeb.Context.ExecuteQuery();
                }
            }

            return returnField;
        }

        /// <summary>
        /// Add web part to a wiki style page
        /// </summary>
        /// <param name="web">Site to be processed - can be root web or sub site</param>
        /// <param name="folder">System name of the wiki page library - typically sitepages</param>
        /// <param name="webPart">Information about the web part to insert</param>
        /// <param name="page">Page to add the web part on</param>
        /// <param name="row">Row of the wiki table that should hold the inserted web part</param>
        /// <param name="col">Column of the wiki table that should hold the inserted web part</param>
        /// <param name="addSpace">Does a blank line need to be added after the web part (to space web parts)</param>
        /// <exception cref="System.ArgumentException">Thrown when folder or page is a zero-length string or contains only white space</exception>
        /// <exception cref="System.ArgumentNullException">Thrown when folder, webPart or page is null</exception>
        public static void AddWebPartToWikiPage(this Web web, string folder, WebPartEntity webPart, string page, int row, int col, bool addSpace, bool something)
        {
            if (string.IsNullOrEmpty(folder))
            {
                throw (folder == null)
                  ? new ArgumentNullException("folder")
                  : new ArgumentException("Empty string for folder", "folder");
            }

            if (webPart == null)
            {
                throw new ArgumentNullException("webPart");
            }

            if (string.IsNullOrEmpty(page))
            {
                throw (page == null)
                  ? new ArgumentNullException("page")
                  : new ArgumentException("Empty string for page", "page");
            }

            if (!web.IsObjectPropertyInstantiated("ServerRelativeUrl"))
            {
                web.Context.Load(web, w => w.ServerRelativeUrl);
                web.Context.ExecuteQueryRetry();
            }

            var webServerRelativeUrl = UrlUtility.EnsureTrailingSlash(web.ServerRelativeUrl);
            var serverRelativeUrl = UrlUtility.Combine(folder, page);
            AddWebPartToWikiPage(web, webServerRelativeUrl + serverRelativeUrl, webPart, row, col, addSpace);
        }

        /// <summary>
        /// Add web part to a wiki style page
        /// </summary>
        /// <param name="web">Site to be processed - can be root web or sub site</param>
        /// <param name="serverRelativePageUrl">Server relative url of the page to add the webpart to</param>
        /// <param name="webPart">Information about the web part to insert</param>
        /// <param name="row">Row of the wiki table that should hold the inserted web part</param>
        /// <param name="col">Column of the wiki table that should hold the inserted web part</param>
        /// <param name="addSpace">Does a blank line need to be added after the web part (to space web parts)</param>
        /// <exception cref="System.ArgumentException">Thrown when serverRelativePageUrl is a zero-length string or contains only white space</exception>
        /// <exception cref="System.ArgumentNullException">Thrown when serverRelativePageUrl or webPart is null</exception>
        public static void AddWebPartToWikiPage(this Web web, string serverRelativePageUrl, WebPartEntity webPart, int row, int col, bool addSpace)
        {
            if (string.IsNullOrEmpty(serverRelativePageUrl))
            {
                throw (serverRelativePageUrl == null)
                  ? new ArgumentNullException("serverRelativePageUrl")
                  : new ArgumentException("Empty parameter", "serverRelativePageUrl");
            }

            if (webPart == null)
            {
                throw new ArgumentNullException("webPart");
            }

            File webPartPage = web.GetFileByServerRelativeUrl(serverRelativePageUrl);

            if (webPartPage == null)
            {
                return;
            }

            web.Context.Load(webPartPage, wp => wp.ListItemAllFields);
            web.Context.ExecuteQueryRetry();

            string wikiField = (string)webPartPage.ListItemAllFields["WikiField"];

            LimitedWebPartManager limitedWebPartManager = webPartPage.GetLimitedWebPartManager(PersonalizationScope.Shared);
            WebPartDefinition oWebPartDefinition = limitedWebPartManager.ImportWebPart(webPart.WebPartXml);
            WebPartDefinition wpdNew = limitedWebPartManager.AddWebPart(oWebPartDefinition.WebPart, "wpz", 0);
            web.Context.Load(wpdNew);
            web.Context.ExecuteQueryRetry();

            // Close all BR tags
            Regex brRegex = new Regex("<br>", RegexOptions.IgnoreCase);

            wikiField = brRegex.Replace(wikiField, "<br/>");

            XmlDocument xd = new XmlDocument();
            xd.PreserveWhitespace = true;
            xd.LoadXml(wikiField);

            // Sometimes the wikifield content seems to be surrounded by an additional div? 
            XmlElement layoutsTable = xd.SelectSingleNode("div/div/table") as XmlElement;
            if (layoutsTable == null)
            {
                layoutsTable = xd.SelectSingleNode("div/table") as XmlElement;
            }

            XmlElement layoutsZoneInner = layoutsTable.SelectSingleNode(string.Format("tbody/tr[{0}]/td[{1}]/div/div", row, col)) as XmlElement;
            // - space element
            XmlElement space = xd.CreateElement("p");
            XmlText text = xd.CreateTextNode(" ");
            space.AppendChild(text);

            // - wpBoxDiv
            XmlElement wpBoxDiv = xd.CreateElement("div");
            layoutsZoneInner.AppendChild(wpBoxDiv);

            if (addSpace)
            {
                layoutsZoneInner.AppendChild(space);
            }

            XmlAttribute attribute = xd.CreateAttribute("class");
            wpBoxDiv.Attributes.Append(attribute);
            attribute.Value = "ms-rtestate-read ms-rte-wpbox";
            attribute = xd.CreateAttribute("contentEditable");
            wpBoxDiv.Attributes.Append(attribute);
            attribute.Value = "false";
            // - div1
            XmlElement div1 = xd.CreateElement("div");
            wpBoxDiv.AppendChild(div1);
            div1.IsEmpty = false;
            attribute = xd.CreateAttribute("class");
            div1.Attributes.Append(attribute);
            attribute.Value = "ms-rtestate-read " + wpdNew.Id.ToString("D");
            attribute = xd.CreateAttribute("id");
            div1.Attributes.Append(attribute);
            attribute.Value = "div_" + wpdNew.Id.ToString("D");
            // - div2
            XmlElement div2 = xd.CreateElement("div");
            wpBoxDiv.AppendChild(div2);
            div2.IsEmpty = false;
            attribute = xd.CreateAttribute("style");
            div2.Attributes.Append(attribute);
            attribute.Value = "display:none";
            attribute = xd.CreateAttribute("id");
            div2.Attributes.Append(attribute);
            attribute.Value = "vid_" + wpdNew.Id.ToString("D");

            ListItem listItem = webPartPage.ListItemAllFields;
            listItem["WikiField"] = xd.OuterXml;
            listItem.Update();
            web.Context.ExecuteQueryRetry();

        }

    }
}
