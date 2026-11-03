namespace Mnemarr.Integration.Test;

/// <summary>One application with a writable data folder, shared by the tests that only read from it.</summary>
[CollectionDefinition("Host")]
public sealed class HostCollection : ICollectionFixture<MnemarrFactory>;

/// <summary>Tests that set process-wide environment variables run alone, so they cannot leak into another host.</summary>
[CollectionDefinition("Environment variables", DisableParallelization = true)]
public sealed class EnvironmentVariablesCollection;
