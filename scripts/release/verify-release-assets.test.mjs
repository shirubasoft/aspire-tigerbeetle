import assert from "node:assert/strict";
import { mkdtemp, mkdir, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { spawnSync } from "node:child_process";
import test from "node:test";

const scriptPath = fileURLToPath(
  new URL("./verify-release-assets.sh", import.meta.url),
);
const packageId = "Shirubasoft.Aspire.Hosting.TigerBeetle";

async function createWorkspace(version = "1.0.0") {
  const workspace = await mkdtemp(join(tmpdir(), "tigerbeetle-release-"));
  const artifacts = join(workspace, "artifacts");
  await mkdir(artifacts);
  await writeFile(join(artifacts, "release-version.txt"), `${version}\n`);
  return { artifacts, workspace };
}

function verify(workspace, version = "1.0.0") {
  return spawnSync("bash", [scriptPath, version], {
    cwd: workspace,
    encoding: "utf8",
  });
}

test("accepts the exact CI package and symbol package", async () => {
  const { artifacts, workspace } = await createWorkspace();
  await writeFile(join(artifacts, `${packageId}.1.0.0.nupkg`), "package");
  await writeFile(join(artifacts, `${packageId}.1.0.0.snupkg`), "symbols");

  assert.equal(verify(workspace).status, 0);
});

test("rejects a mismatched CI package version", async () => {
  const { workspace } = await createWorkspace("1.0.1");
  const result = verify(workspace);

  assert.equal(result.status, 1);
  assert.match(result.stderr, /CI packaged version 1\.0\.1/);
});

test("rejects a missing package or symbol package", async () => {
  const { artifacts, workspace } = await createWorkspace();
  await writeFile(join(artifacts, `${packageId}.1.0.0.nupkg`), "package");
  const result = verify(workspace);

  assert.equal(result.status, 1);
  assert.match(result.stderr, /\.snupkg/);
});
