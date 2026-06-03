// ADR: Exposes internals (FogGrid, state) to the Fog test assembly only.
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Readymade.PostProcessing.Fog.Tests")]
