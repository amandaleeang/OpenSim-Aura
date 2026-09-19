using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mono.Addins;

[assembly: AssemblyTitle("OpenSim.Addons.UUIDDHT")]
[assembly: AssemblyDescription("UUID DHT fallback for intra-grid user and group lookup")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("http://opensimulator.org")]
[assembly: AssemblyProduct("OpenSim.Addons.UUIDDHT")]
[assembly: AssemblyCopyright("Copyright (c) OpenSimulator.org Developers")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: ComVisible(false)]
[assembly: Guid("b7e41a90-0000-0000-0000-000000000000")]
[assembly: AssemblyVersion(OpenSim.VersionInfo.AssemblyVersionNumber)]

[assembly: Addin("OpenSim.UUIDDHT", OpenSim.VersionInfo.VersionNumber)]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]
[assembly: InternalsVisibleTo("OpenSim.Addons.UUIDDHT.Tests")]
