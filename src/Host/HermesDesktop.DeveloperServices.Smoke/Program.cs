if (args.Contains("--real", StringComparer.Ordinal))
    await RealDeveloperIntegrationSmoke.RunAsync();
else
    await DeveloperDebugBridgeModuleSmoke.RunAsync();
