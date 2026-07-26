import fs from "node:fs";

const [assetsPath, runtime] = process.argv.slice(2);
if (!assetsPath || !runtime) {
  throw new Error("Usage: node classify-nuget-assets.mjs <project.assets.json> <runtime>");
}

const assets = JSON.parse(fs.readFileSync(assetsPath, "utf8"));
const targetEntry = Object.entries(assets.targets ?? {}).find(([name]) =>
  name.endsWith(`/${runtime}`),
);
if (!targetEntry) {
  throw new Error(`No NuGet target exists for ${runtime}.`);
}

const hasPayloadAsset = (group) =>
  Object.keys(group ?? {}).some((path) => !path.endsWith("/_._"));

const packages = Object.entries(targetEntry[1])
  .filter(
    ([, value]) =>
      value.type === "package" &&
      (hasPayloadAsset(value.runtime) ||
        hasPayloadAsset(value.native) ||
        hasPayloadAsset(value.runtimeTargets)),
  )
  .map(([name]) => name.toLowerCase())
  .sort();

process.stdout.write(JSON.stringify(packages));
