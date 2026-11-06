#!/usr/bin/env node
'use strict';

const fs = require('node:fs');
const path = require('node:path');

const [sourceArg, destinationArg, reportArg] = process.argv.slice(2);
if (!sourceArg || !destinationArg) {
  console.error('Usage: node Build-FilesystemMcpBundle.cjs <filesystem-package-dir> <destination-dir> [report-file]');
  process.exit(2);
}

const sourceRoot = fs.realpathSync(sourceArg);
const destinationRoot = path.resolve(destinationArg);
const reportPath = reportArg ? path.resolve(reportArg) : null;
const copiedPackages = new Map();
const activeSources = new Set();
let copiedFiles = 0;
let copiedBytes = 0;

function readManifest(packageDir) {
  const manifestPath = path.join(packageDir, 'package.json');
  if (!fs.existsSync(manifestPath)) throw new Error(`Missing package.json: ${packageDir}`);
  return JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
}

function packageNameParts(name) {
  return name.split('/');
}

function resolvePackage(name, fromDirectory) {
  const parts = packageNameParts(name);
  let current = path.resolve(fromDirectory);
  while (true) {
    const candidate = path.join(current, 'node_modules', ...parts);
    const manifest = path.join(candidate, 'package.json');
    if (fs.existsSync(manifest)) {
      try {
        const real = fs.realpathSync(candidate);
        const parsed = readManifest(real);
        if (!parsed.name || parsed.name === name) return real;
      } catch (_) { /* continue searching parent node_modules */ }
    }
    const parent = path.dirname(current);
    if (parent === current) break;
    current = parent;
  }
  return null;
}

function copyPackage(sourceDirectory, targetDirectory, optional = false) {
  const realSource = fs.realpathSync(sourceDirectory);
  const manifest = readManifest(realSource);
  const packageKey = `${manifest.name || path.basename(realSource)}@${manifest.version || 'unknown'}`;

  if (fs.existsSync(targetDirectory)) fs.rmSync(targetDirectory, { recursive: true, force: true });
  fs.mkdirSync(targetDirectory, { recursive: true });

  // Copy package contents but deliberately omit its entire node_modules tree;
  // dependencies are re-added below from the exact production dependency graph.
  fs.cpSync(realSource, targetDirectory, {
    recursive: true,
    force: true,
    dereference: true,
    filter: (entry) => {
      const relative = path.relative(realSource, entry);
      if (!relative) return true;
      const segments = relative.split(path.sep);
      if (segments.includes('node_modules') || segments.includes('.git') || segments.includes('.npm')) return false;
      return true;
    }
  });

  copiedPackages.set(realSource, packageKey);
  if (activeSources.has(realSource)) return;
  activeSources.add(realSource);

  const dependencies = { ...(manifest.dependencies || {}), ...(manifest.optionalDependencies || {}) };
  const optionalNames = new Set(Object.keys(manifest.optionalDependencies || {}));
  const peerMeta = manifest.peerDependenciesMeta || {};
  for (const [name, range] of Object.entries(manifest.peerDependencies || {})) {
    if (!peerMeta[name]?.optional) dependencies[name] = range;
    else optionalNames.add(name);
  }
  const bundled = manifest.bundleDependencies || manifest.bundledDependencies || [];
  for (const name of bundled) if (!(name in dependencies)) dependencies[name] = '*';

  for (const name of Object.keys(dependencies)) {
    const dependencySource = resolvePackage(name, realSource);
    if (!dependencySource) {
      if (optionalNames.has(name)) continue;
      throw new Error(`Cannot resolve production dependency "${name}" required by ${packageKey}`);
    }
    const dependencyTarget = path.join(targetDirectory, 'node_modules', ...packageNameParts(name));
    copyPackage(dependencySource, dependencyTarget, optionalNames.has(name));
  }

  activeSources.delete(realSource);
}

function countTree(directory) {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const full = path.join(directory, entry.name);
    if (entry.isDirectory()) countTree(full);
    else if (entry.isFile()) {
      copiedFiles++;
      copiedBytes += fs.statSync(full).size;
    }
  }
}

try {
  const rootManifest = readManifest(sourceRoot);
  fs.rmSync(destinationRoot, { recursive: true, force: true });
  copyPackage(sourceRoot, destinationRoot);
  copiedFiles = 0;
  copiedBytes = 0;
  countTree(destinationRoot);
  const packageLines = [...copiedPackages.entries()].map(([dir, key]) => `${key}\t${dir}`);
  const report = [
    `Root: ${rootManifest.name}@${rootManifest.version}`,
    `Unique source packages: ${copiedPackages.size}`,
    `Copied files: ${copiedFiles}`,
    `Copied size: ${(copiedBytes / 1024 / 1024).toFixed(2)} MiB`,
    '',
    'Resolved production package graph:',
    ...packageLines
  ].join('\n');
  if (reportPath) {
    fs.mkdirSync(path.dirname(reportPath), { recursive: true });
    fs.writeFileSync(reportPath, report, 'utf8');
  }
  console.log(`Filesystem MCP bundle: ${copiedPackages.size} packages, ${copiedFiles} files, ${(copiedBytes / 1024 / 1024).toFixed(2)} MiB.`);
} catch (error) {
  console.error(error && error.stack ? error.stack : String(error));
  process.exit(1);
}