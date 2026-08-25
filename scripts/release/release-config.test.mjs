import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const releaseConfig = JSON.parse(
  await readFile(new URL("../../.releaserc.json", import.meta.url), "utf8"),
);

test("releases main with conventional commits and v-prefixed tags", () => {
  assert.deepEqual(releaseConfig.branches, ["main"]);
  assert.equal(releaseConfig.tagFormat, "v${version}");
  assert.deepEqual(
    releaseConfig.plugins.slice(0, 2).map(([name]) => name),
    [
      "@semantic-release/commit-analyzer",
      "@semantic-release/release-notes-generator",
    ],
  );
});

test("publishes the exact package verified from the CI artifact", () => {
  const execPlugin = releaseConfig.plugins.find(
    ([name]) => name === "@semantic-release/exec",
  );
  assert.ok(execPlugin);

  const options = execPlugin[1];
  assert.match(options.verifyConditionsCmd, /NUGET_API_KEY/);
  assert.match(
    options.verifyReleaseCmd,
    /scripts\/release\/verify-release-assets\.sh/,
  );
  assert.match(
    options.publishCmd,
    /Shirubasoft\.Aspire\.Hosting\.TigerBeetle\.\$\{nextRelease\.version\}\.nupkg/,
  );
  assert.match(options.publishCmd, /--symbol-source/);
  assert.match(options.publishCmd, /--skip-duplicate/);
});

test("attaches the NuGet package to the GitHub release", () => {
  const githubPlugin = releaseConfig.plugins.find(
    ([name]) => name === "@semantic-release/github",
  );
  assert.ok(githubPlugin);
  assert.deepEqual(githubPlugin[1].assets, [
    {
      path: "artifacts/*.nupkg",
      label: "NuGet package",
    },
    {
      path: "artifacts/*.snupkg",
      label: "NuGet symbol package",
    },
  ]);
});
