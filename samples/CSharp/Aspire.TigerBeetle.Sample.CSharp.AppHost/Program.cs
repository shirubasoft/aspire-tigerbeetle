var builder = DistributedApplication.CreateBuilder(args);

var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
    .WithDataVolume()
    .WithCacheGrid("256MiB")
    .PublishAsDockerComposeService((_, service) =>
    {
        service.SecurityOpt.Add("seccomp=unconfined");
    });

builder.AddProject<Projects.Aspire_TigerBeetle_Sample_CSharp_Client>("client")
    .WithReference(tigerBeetle)
    .WaitFor(tigerBeetle)
    .WithHttpEndpoint()
    .WithExternalHttpEndpoints();

builder.AddDockerComposeEnvironment("docker");

builder.Build().Run();
