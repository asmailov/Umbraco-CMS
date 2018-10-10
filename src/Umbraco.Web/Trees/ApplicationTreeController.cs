﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http.Formatting;
using System.Threading.Tasks;
using System.Web.Http;
using Umbraco.Core;
using Umbraco.Core.Models;
using Umbraco.Core.Services;
using Umbraco.Web.Composing;
using Umbraco.Web.Models.Trees;
using Umbraco.Web.Mvc;
using Umbraco.Web.WebApi;
using Umbraco.Web.WebApi.Filters;
using Constants = Umbraco.Core.Constants;

namespace Umbraco.Web.Trees
{
    [AngularJsonOnlyConfiguration]
    [PluginController("UmbracoTrees")]
    public class ApplicationTreeController : UmbracoAuthorizedApiController
    {
        private static readonly Lazy<IEnumerable<IGrouping<string, (Type, string)>>> CoreTrees
            = new Lazy<IEnumerable<IGrouping<string, (Type, string)>>>(() =>
                Current.Services.ApplicationTreeService.GetAllTypes()
                .Select(x => (TreeType: x, TreeGroup: x.GetCustomAttribute<CoreTreeAttribute>(false)?.TreeGroup))
                .GroupBy(x => x.TreeGroup)
                .ToList());
    
        
        /// <summary>
        /// Returns the tree nodes for an application
        /// </summary>
        /// <param name="application">The application to load tree for</param>
        /// <param name="tree">An optional single tree alias, if specified will only load the single tree for the request app</param>
        /// <param name="queryStrings"></param>
        /// <param name="onlyInitialized">An optional bool (defaults to true), if set to false it will also load uninitialized trees</param>
        /// <returns></returns>
        [HttpQueryStringFilter("queryStrings")]
        public async Task<IEnumerable<SectionRootNode>> GetApplicationTrees(string application, string tree, FormDataCollection queryStrings, bool onlyInitialized = true)
        {
            application = application.CleanForXss();

            var rootNodeGroups = new List<SectionRootNode>();

            if (string.IsNullOrEmpty(application)) throw new HttpResponseException(HttpStatusCode.NotFound);

            var rootId = Constants.System.Root.ToString(CultureInfo.InvariantCulture);

            //find all tree definitions that have the current application alias
            var appTrees = Services.ApplicationTreeService.GetApplicationTrees(application, onlyInitialized).ToArray();

            if (string.IsNullOrEmpty(tree) == false || appTrees.Length <= 1)
            {
                var apptree = string.IsNullOrEmpty(tree) == false
                    ? appTrees.SingleOrDefault(x => x.Alias == tree)
                    : appTrees.SingleOrDefault();

                if (apptree == null) throw new HttpResponseException(HttpStatusCode.NotFound);

                var result = await GetRootForSingleAppTree(
                    apptree,
                    Constants.System.Root.ToString(CultureInfo.InvariantCulture),
                    queryStrings,
                    application);

                //this will be null if it cannot convert to ta single root section
                if (result != null)
                {
                    rootNodeGroups.Add(result);
                    return rootNodeGroups;
                }
            }

            var collection = new TreeNodeCollection();
            foreach (var apptree in appTrees)
            {
                //return the root nodes for each tree in the app
                var rootNode = await GetRootForMultipleAppTree(apptree, queryStrings);
                //This could be null if the tree decides not to return it's root (i.e. the member type tree does this when not in umbraco membership mode)
                if (rootNode != null)
                {
                    collection.Add(rootNode);
                }
            }

            //Don't apply fancy grouping logic futher down, if we are not 'settings' section
            if(application != Constants.Applications.Settings)
            {
                var multiTree = SectionRootNode.CreateMultiTreeSectionRoot(rootId, collection);
                multiTree.Name = Services.TextService.Localize("sections/" + application);

                rootNodeGroups.Add(multiTree);
                return rootNodeGroups;
            }

            //For settings section only
            //Group trees by [CoreTree] attribute

            //Core Trees contains all trees for all sections/applications
            foreach(var treeSectionGroup in CoreTrees.Value)
            {
                var treeGroupName = treeSectionGroup.Key;

                var groupNodeCollection = new TreeNodeCollection();

                //Only add trees to a new collection if they are from 'settings'
                foreach (var treeItem in treeSectionGroup)
                {
                    //Item1 tuple - is the type from all tree types
                    var treeItemType = treeItem.Item1;

                    var findAppTree = appTrees.SingleOrDefault(x => x.GetRuntimeType() == treeItemType);
                    if (findAppTree != null)
                    {
                        //Now we need to get the 'TreeNode' which is in 'collection'
                        var treeItemNode = collection.SingleOrDefault(x => x.AdditionalData["treeAlias"].ToString() == findAppTree.Alias);

                        if (treeItemNode != null)
                        {
                            //Add to a new list/collection
                            groupNodeCollection.Add(treeItemNode);
                        }
                    }
                }

                //If treeGroupName == null then its third party
                if (treeGroupName == null)
                {
                    //This is used for the localisation key
                    //treeHeaders/thirdPartyGroup
                    treeGroupName = "thirdPartyGroup";
                }

                if (groupNodeCollection.Any())
                {
                    var groupRoot = SectionRootNode.CreateMultiTreeSectionRoot(rootId, groupNodeCollection);
                    groupRoot.Name = Services.TextService.Localize("treeHeaders/" + treeGroupName);

                    rootNodeGroups.Add(groupRoot);
                }
            }

            return rootNodeGroups.OrderBy(x => x.Name);
        }

        /// <summary>
        /// Get the root node for an application with multiple trees
        /// </summary>
        /// <param name="configTree"></param>
        /// <param name="queryStrings"></param>
        /// <returns></returns>
        private async Task<TreeNode> GetRootForMultipleAppTree(ApplicationTree configTree, FormDataCollection queryStrings)
        {
            if (configTree == null) throw new ArgumentNullException(nameof(configTree));
            try
            {
                var byControllerAttempt = await configTree.TryGetRootNodeFromControllerTree(queryStrings, ControllerContext);
                if (byControllerAttempt.Success)
                {
                    return byControllerAttempt.Result;
                }
            }
            catch (HttpResponseException)
            {
                //if this occurs its because the user isn't authorized to view that tree, in this case since we are loading multiple trees we
                //will just return null so that it's not added to the list.
                return null;
            }

            var legacyAttempt = configTree.TryGetRootNodeFromLegacyTree(queryStrings, Url, configTree.ApplicationAlias);
            if (legacyAttempt.Success)
            {
                return legacyAttempt.Result;
            }

            throw new ApplicationException("Could not get root node for tree type " + configTree.Alias);
        }

        /// <summary>
        /// Get the root node for an application with one tree
        /// </summary>
        /// <param name="configTree"></param>
        /// <param name="id"></param>
        /// <param name="queryStrings"></param>
        /// <param name="application"></param>
        /// <returns></returns>
        private async Task<SectionRootNode> GetRootForSingleAppTree(ApplicationTree configTree, string id, FormDataCollection queryStrings, string application)
        {
            var rootId = Constants.System.Root.ToString(CultureInfo.InvariantCulture);
            if (configTree == null) throw new ArgumentNullException(nameof(configTree));
            var byControllerAttempt = configTree.TryLoadFromControllerTree(id, queryStrings, ControllerContext);
            if (byControllerAttempt.Success)
            {
                var rootNode = await configTree.TryGetRootNodeFromControllerTree(queryStrings, ControllerContext);
                if (rootNode.Success == false)
                {
                    //This should really never happen if we've successfully got the children above.
                    throw new InvalidOperationException("Could not create root node for tree " + configTree.Alias);
                }

                //if the root node has a route path, we cannot create a single root section because by specifying the route path this would
                //override the dashboard route and that means there can be no dashboard for that section which is a breaking change.
                if (string.IsNullOrWhiteSpace(rootNode.Result.RoutePath) == false
                    && rootNode.Result.RoutePath != "#"
                    && rootNode.Result.RoutePath != application)
                {
                    //null indicates this cannot be converted
                    return null;
                }

                var sectionRoot = SectionRootNode.CreateSingleTreeSectionRoot(
                    rootId,
                    rootNode.Result.ChildNodesUrl,
                    rootNode.Result.MenuUrl,
                    rootNode.Result.Name,
                    byControllerAttempt.Result);

                //This can't be done currently because the root will default to routing to a dashboard and if we disable dashboards for a section
                //that is really considered a breaking change. See above.
                //sectionRoot.RoutePath = rootNode.Result.RoutePath;

                foreach (var d in rootNode.Result.AdditionalData)
                {
                    sectionRoot.AdditionalData[d.Key] = d.Value;
                }
                return sectionRoot;

            }
            var legacyAttempt = configTree.TryLoadFromLegacyTree(id, queryStrings, Url, configTree.ApplicationAlias);
            if (legacyAttempt.Success)
            {
                var sectionRoot = SectionRootNode.CreateSingleTreeSectionRoot(
                   rootId,
                   "", //TODO: I think we'll need this in this situation!
                   Url.GetUmbracoApiService<LegacyTreeController>("GetMenu", rootId)
                        + "&parentId=" + rootId
                        + "&treeType=" + application
                        + "&section=" + application,
                   "", //TODO: I think we'll need this in this situation!
                   legacyAttempt.Result);


                sectionRoot.AdditionalData.Add("treeAlias", configTree.Alias);
                return sectionRoot;
            }

            throw new ApplicationException("Could not render a tree for type " + configTree.Alias);
        }
    }
}
