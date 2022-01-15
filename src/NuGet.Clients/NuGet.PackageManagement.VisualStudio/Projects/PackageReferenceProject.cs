// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Threading;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.PackageManagement.VisualStudio.Utility;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.ProjectManagement;
using NuGet.ProjectManagement.Projects;
using NuGet.ProjectModel;
using TransitiveEntry = System.Collections.Generic.IDictionary<NuGet.Frameworks.FrameworkRuntimePair, System.Collections.Generic.IList<NuGet.Packaging.PackageReference>>;

namespace NuGet.PackageManagement.VisualStudio
{
    /// <summary>
    /// An implementation of <see cref="NuGetProject"/> that interfaces with VS project APIs to coordinate
    /// packages in a package reference style project.
    /// </summary>
    public abstract class PackageReferenceProject : BuildIntegratedNuGetProject
    {
        internal static readonly Comparer<PackageReference> PackageReferenceMergeComparer = Comparer<PackageReference>.Create((a, b) => a?.PackageIdentity?.CompareTo(b.PackageIdentity) ?? 1);

        private protected readonly Dictionary<PackageIdentity, TransitiveEntry> TransitiveOriginsCache = new();

        private readonly protected string _projectName;
        private readonly protected string _projectUniqueName;
        private readonly protected string _projectFullPath;

        private protected DateTime _lastTimeAssetsModified;
        private protected WeakReference<PackageSpec> _lastPackageSpec;

        protected bool IsInstalledAndTransitiveComputationNeeded { get; set; } = true;

        protected PackageReferenceProject(
            string projectName,
            string projectUniqueName,
            string projectFullPath)
        {
            _projectName = projectName;
            _projectUniqueName = projectUniqueName;
            _projectFullPath = projectFullPath;
        }

        public override async Task<string> GetAssetsFilePathAsync()
        {
            return await GetAssetsFilePathAsync(shouldThrow: true);
        }

        public override async Task<string> GetAssetsFilePathOrNullAsync()
        {
            return await GetAssetsFilePathAsync(shouldThrow: false);
        }

        private protected abstract Task<string> GetAssetsFilePathAsync(bool shouldThrow);

        public override string ProjectName => _projectName;

        public override async Task<IReadOnlyList<PackageSpec>> GetPackageSpecsAsync(DependencyGraphCacheContext context)
        {
            (IReadOnlyList<PackageSpec> dgSpec, IReadOnlyList<IAssetsLogMessage> _) = await GetPackageSpecsAndAdditionalMessagesAsync(context);
            return dgSpec;
        }

        /// <summary>
        /// Gets the both the installed (top level) and transitive package references for this project.
        /// Returns the package reference as two separate lists (installed and transitive).
        /// </summary>
        /// <param name="token">Cancellation token</param>
        /// <returns>A <see cref="ProjectPackages"/> object with two lists: Installed and transitive packages</returns>
        public abstract Task<ProjectPackages> GetInstalledAndTransitivePackagesAsync(CancellationToken token);

        private protected IEnumerable<PackageReference> GetPackageReferences(
            IEnumerable<LibraryDependency> libraries,
            NuGetFramework targetFramework,
            Dictionary<string, ProjectInstalledPackage> installedPackages,
            IList<LockFileTarget> targets)
        {
            return libraries
                .Where(library => library.LibraryRange.TypeConstraint == LibraryDependencyTarget.Package)
                .Select(library => new BuildIntegratedPackageReference(library, targetFramework, GetPackageReferenceUtility.UpdateResolvedVersion(library, targetFramework, targets, installedPackages)));
        }

        private protected IReadOnlyList<PackageReference> GetTransitivePackageReferences(
            NuGetFramework targetFramework,
            Dictionary<string, ProjectInstalledPackage> installedPackages,
            Dictionary<string, ProjectInstalledPackage> transitivePackages,
            IList<LockFileTarget> targets)
        {
            // If the assets files has not been updated, return the cached transitive packages
            if (targets == null)
            {
                return transitivePackages
                    .Select(package => new PackageReference(package.Value.InstalledPackage, targetFramework))
                    .ToList();
            }
            else
            {
                return targets
                    .SelectMany(target => target.Libraries)
                    .Where(library => library.Type == LibraryType.Package)
                    .SelectMany(library => GetPackageReferenceUtility.UpdateTransitiveDependencies(library, targetFramework, targets, installedPackages, transitivePackages))
                    .Select(packageIdentity => new PackageReference(packageIdentity, targetFramework))
                    .ToList();
            }
        }

        /// <summary>
        /// Get All Installed packages that transitively install a given transitive package in this project
        /// </summary>
        /// <param name="transitivePackage">Identity of given transtive package</param>
        /// <param name="ct">Cancellation Token</param>
        /// <returns>A dictionary, indexed by Framework/Runtime-ID with all top (installed)
        /// packages that depends on given transitive package, or <c>null</c> if none found</returns>
        /// <remarks>Computes all transitive origings for each Framework/Runtime-ID combiation. Runtime-ID can be <c>null</c>.
        /// Transitive origins are calculated using a Depth First Search algorithm on all direct dependencies exhaustively</remarks>
        internal TransitiveEntry GetTransitivePackageOrigin(PackageReference transitivePackage, IReadOnlyList<PackageReference> installedPackages, IList<LockFileTarget> targetsList, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (!IsInstalledAndTransitiveComputationNeeded)
            {
                // Assets file has not changed, look at transtive origin cache
                // 2.1 Look for a transitive cached entry and return that entry
                TransitiveEntry cacheEntry = GetCachedTransitiveOrigin(transitivePackage.PackageIdentity);
                return cacheEntry;
            }

            // Assets file changed, recompute transitive origins
            ClearCachedTransitiveOrigins();

            // Otherwise, find all Transitive origin and update cache
            var memoryVisited = new HashSet<PackageIdentity>();

            // 3. For each target framework graph (Framework, RID)-pair:
            foreach (LockFileTarget targetFxGraph in targetsList)
            {
                var key = new FrameworkRuntimePair(targetFxGraph.TargetFramework, targetFxGraph.RuntimeIdentifier);

                foreach (var directPkg in installedPackages) // 3.1 For each direct dependency d:
                {
                    memoryVisited.Clear();
                    // 3.1.1 Do DFS to mark directPkg as a transitive origin over all transitive dependencies found
                    MarkTransitiveOrigin(directPkg, directPkg, targetFxGraph, memoryVisited, key, ct);
                }
            }

            // 4. return cached result for specific transitive dependency
            return GetCachedTransitiveOrigin(transitivePackage.PackageIdentity);
        }

        /// <summary>
        /// Returns <see cref="PackageSpec"/> found in assets file (project.assets.json)
        /// </summary>
        /// <param name="token">Cancellation token</param>
        /// <returns>An <see cref="PackageSpec"/> object</returns>
        /// <remarks>Projects need to be NuGet-restored before calling this function</remarks>
        internal async Task<PackageSpec> GetCachedPackageSpecAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            string assetsFilePath = await GetAssetsFilePathAsync();
            var assets = new FileInfo(assetsFilePath);

            PackageSpec currentPackageSpec = await GetPackageSpecAsync(token);
            PackageSpec cachedPackageSpec = null;
            _ = _lastPackageSpec != null && _lastPackageSpec.TryGetTarget(out cachedPackageSpec);

            bool cacheMissAssets = (assets.Exists && assets.LastWriteTimeUtc > _lastTimeAssetsModified);

            if (cacheMissAssets || IsPackageSpecDifferent(currentPackageSpec, cachedPackageSpec))
            {
                _lastTimeAssetsModified = assets.LastWriteTimeUtc;
                _lastPackageSpec = new WeakReference<PackageSpec>(currentPackageSpec);
                IsInstalledAndTransitiveComputationNeeded = true;
            }

            return currentPackageSpec;
        }

        /// <summary>
        /// Obtains targets section from project assets file (project.assets.json)
        /// </summary>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>A list of dependencies, indexed by framework/RID</returns>
        /// <remarks>Assets file reading occurs in a background thread</remarks>
        /// <seealso cref="GetAssetsFilePathAsync"/>
        protected async ValueTask<IList<LockFileTarget>> GetTargetsListAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            await TaskScheduler.Default;

            string assetsFilePath = await GetAssetsFilePathAsync();
            LockFile lockFile = LockFileUtilities.GetLockFile(assetsFilePath, NullLogger.Instance);

            return lockFile?.Targets;
        }

        /// <summary>
        /// Runs Depth First Search recursively to mark current and dependend nodes with top dependency
        /// </summary>
        /// <param name="top">Top, Direct dependency</param>
        /// <param name="current">Current package/node to visit</param>
        /// <param name="graph">Package dependency graph, from assets file</param>
        /// <param name="visited">Dictionary to remember visited nodes</param>
        /// <param name="fxRidEntry">Framework/Runtime-ID associated with current <paramref name="graph"/></param>
        private void MarkTransitiveOrigin(PackageReference top, PackageReference current, LockFileTarget graph, HashSet<PackageIdentity> visited, FrameworkRuntimePair fxRidEntry, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            LockFileTargetLibrary node = default;

            // Find first target node that matches current
            foreach (LockFileTargetLibrary lib in graph.Libraries)
            {
                if (lib.Type == LibraryType.Package.Value
                    && string.Equals(lib.Name, current.PackageIdentity.Id, StringComparison.OrdinalIgnoreCase)
                    && ((current.HasAllowedVersions && current.AllowedVersions.Satisfies(lib.Version)) ||
                        (current.PackageIdentity.HasVersion && current.PackageIdentity.Version.Equals(lib.Version))))
                {
                    node = lib;
                    break;
                }
            }

            if (node != default)
            {
                visited.Add(current.PackageIdentity); // visited

                // Update cache
                TransitiveEntry cachedEntry = GetCachedTransitiveOrigin(current.PackageIdentity);

                if (cachedEntry == null)
                {
                    cachedEntry = new Dictionary<FrameworkRuntimePair, IList<PackageReference>>
                    {
                        [fxRidEntry] = new List<PackageReference>()
                    };
                }

                if (!cachedEntry.ContainsKey(fxRidEntry))
                {
                    cachedEntry[fxRidEntry] = new List<PackageReference>();
                }

                if (!cachedEntry[fxRidEntry].Contains(top))
                {
                    cachedEntry[fxRidEntry].Add(top);
                }

                SetCachedTransitiveOrigin(current.PackageIdentity, cachedEntry);

                foreach (PackageDependency dep in node.Dependencies.ToList()) // Casting to list to prevent backing allocations
                {
                    // Create PackageReference object as a data-model based on dependency
                    var pkgChild = new PackageReference(
                        identity: new PackageIdentity(dep.Id, dep.VersionRange.MinVersion),
                        targetFramework: fxRidEntry.Framework,
                        userInstalled: false,
                        developmentDependency: false,
                        requireReinstallation: false,
                        allowedVersions: dep.VersionRange);

                    if (!visited.Contains(pkgChild.PackageIdentity))
                    {
                        MarkTransitiveOrigin(top, pkgChild, graph, visited, fxRidEntry, token);
                    }
                }
            }
        }

        /// <summary>
        /// Generates a cache key for Transitive Originas cache
        /// </summary>
        /// <param name="transitivePackage"></param>
        /// <returns>A string with given key</returns>
        /// <seealso cref="GetCachedTransitiveOrigin(PackageIdentity)"/>
        /// <seealso cref="SetCachedTransitiveOrigin(PackageIdentity, TransitiveEntry)"/>
        internal PackageIdentity GetTransitiveCacheKey(PackageIdentity transitivePackage)
        {
            return transitivePackage;
        }

        /// <summary>
        /// Obtains cached entry for a given transitive package
        /// </summary>
        /// <param name="transitivePackage">Identity of transitive package</param>
        /// <returns>A <see cref="TransitiveEntry"/> object, or <c>null</c> if not found</returns>
        /// <seealso cref="ClearCachedTransitiveOrigins"/>
        /// <seealso cref="SetCachedTransitiveOrigin(PackageIdentity, TransitiveEntry)"/>
        internal TransitiveEntry GetCachedTransitiveOrigin(PackageIdentity transitivePackage)
        {
            var key = GetTransitiveCacheKey(transitivePackage);

            if (TransitiveOriginsCache.ContainsKey(key))
            {
                return TransitiveOriginsCache[key];
            }

            return null;
        }

        /// <summary>
        /// Replaces cached entry for a given transitive package with a <see cref="TransitiveEntry"/>
        /// </summary>
        /// <param name="transitivePackage">Identity of transitive package</param>
        /// <param name="origins">Packages identified as package origins</param>
        /// <seealso cref="ClearCachedTransitiveOrigins"/>
        /// <seealso cref="GetCachedTransitiveOrigin(PackageIdentity)"/>
        internal void SetCachedTransitiveOrigin(PackageIdentity transitivePackage, TransitiveEntry origins)
        {
            var key = GetTransitiveCacheKey(transitivePackage);
            TransitiveOriginsCache[key] = origins;
        }

        /// <summary>
        /// Clears Transitive Origins cache
        /// </summary>
        /// <seealso cref="GetCachedTransitiveOrigin(PackageIdentity)"/>
        /// <seealso cref="SetCachedTransitiveOrigin(PackageIdentity, TransitiveEntry)"/>
        internal void ClearCachedTransitiveOrigins()
        {
            TransitiveOriginsCache.Clear();
            IsInstalledAndTransitiveComputationNeeded = true;
        }

        internal static TransitivePackageReference MergeTransitiveOrigin(PackageReference currentPackage, TransitiveEntry transitiveEntry)
        {
            var transitiveOrigins = new SortedSet<PackageReference>(PackageReferenceMergeComparer);

            transitiveEntry?.Keys?.ForEach(key => transitiveOrigins.AddRange(transitiveEntry[key]));

            var transitivePR = new TransitivePackageReference(currentPackage)
            {
                TransitiveOrigins = transitiveOrigins,
            };

            return transitivePR;
        }

        /// <summary>
        /// Obtains <see cref="PackageSpec"/> object from assets file from disk
        /// </summary>
        /// <param name="ct">Cancellation token</param>
        /// <returns>A <see cref="PackageSpec"/> filled from assets file on disk</returns>
        /// <remarks>Each project implementation is responsible of gathering <see cref="PackageSpec"/> info</remarks>
        internal abstract ValueTask<PackageSpec> GetPackageSpecAsync(CancellationToken ct);

        /// <summary>
        /// Decides wether cached <see cref="PackageSpec"/> differs from assets file on disk
        /// </summary>
        /// <param name="actual">A <see cref="PackageSpec"/> read from disk</param>
        /// <param name="cached">Cached <see cref="PackageSpec"/></param>
        /// <returns><c>true</c> if current <see cref="PackageSpec"/> differs from cached objects</returns>
        internal abstract bool IsPackageSpecDifferent(PackageSpec actual, PackageSpec cached);

        /// <summary>
        /// Clears Cached Transitive package prigins, Installed packages and Transitive packages
        /// </summary>
        internal abstract void ClearCache();
    }
}
