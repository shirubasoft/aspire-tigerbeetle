import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const tigerBeetle = await builder.addTigerBeetle('tigerbeetle');
await tigerBeetle.withDataVolume();
await tigerBeetle.withCacheGrid('256MiB');

const client = await builder.addNodeApp('client', './client', 'dist/index.js');
await client.withReference(tigerBeetle);
await client.waitFor(tigerBeetle);
await client.withHttpEndpoint({ env: 'PORT' });
await client.withExternalHttpEndpoints();

await builder.build().run();
