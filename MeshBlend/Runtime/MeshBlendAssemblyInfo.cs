// ADR: Exposes internals (pass, future store) to the MeshBlend test assembly only.
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Readymade.PostProcessing.MeshBlend.Tests")]
