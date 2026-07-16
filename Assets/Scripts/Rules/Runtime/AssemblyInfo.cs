using System.Runtime.CompilerServices;

// KillCamRecorder.SampleNow is deliberately internal: it's the explicit-time seam the ring-buffer
// tests drive, with no business being public API. The PlayMode tests are a separate assembly, so
// they need this to reach it.
[assembly: InternalsVisibleTo("RooftopTag.Tests.PlayMode")]
