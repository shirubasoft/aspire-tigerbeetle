import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const tigerBeetle = await builder
  .addTigerBeetle('tigerbeetle')
  .withDataVolume()
  .withCacheGrid('256MiB');

await builder
  .addNodeApp('client', './client', 'dist/index.js')
  .withReference(tigerBeetle)
  .waitFor(tigerBeetle)
  .withHttpEndpoint({ env: 'PORT' })
  .withExternalHttpEndpoints();

await builder.build().run();
