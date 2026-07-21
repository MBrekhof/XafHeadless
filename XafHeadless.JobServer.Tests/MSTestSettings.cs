// Every test in this project drives the ONE shared seeded JobDefinition row / real Hangfire jobs / the
// shared dev DB (unlike XafHeadless.Api.Tests, which is MethodLevel-parallel) -- so the whole assembly
// must run serially. Per-class [DoNotParallelize] on top of this is redundant but harmless.
[assembly: DoNotParallelize]
