// O runtime do jsii (ponte C# → JavaScript do CDK) é um processo por AppDomain: inicialização paralela disputa arquivos.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
