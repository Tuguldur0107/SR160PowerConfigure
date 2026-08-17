using System.Reflection;

// Single source of truth for the app's version. The updater compares this
// against the newest GitHub release tag, so bump it here before tagging a
// release — and keep the tag matching (tag "v1.3" -> "1.3.0.0" here).
//
// Bump this for EVERY published build, not just feature releases: two
// different binaries both claiming 1.2.0.0 (the Aug 3 and Aug 14 builds)
// left installed copies with no way to tell they were out of date.
//
// Starts at 1.2.0 because the repo already published v1.1; anything lower
// would make the app offer that older release as an "update".
[assembly: AssemblyVersion("1.2.1.0")]
[assembly: AssemblyFileVersion("1.2.1.0")]
[assembly: AssemblyTitle("CHIPMO SR160 Power Config")]
[assembly: AssemblyCompany("CHIPMO")]
[assembly: AssemblyProduct("SR160 Power Config")]
