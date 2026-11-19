#!/usr/bin/env node
'use strict';
[process.stdin, process.stdout, process.stderr].forEach(s => { if (s && typeof s.on === 'function') s.on('error', () => {}); });

const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { URL } = require('node:url');

const PROTOCOL_VERSION = '2025-06-18';
const SERVER_INFO = { name: 'Binary MCP', version: '1.0.0' };
const MANAGER_API_KEY = String(process.env.MCPHOTPLUG_MANAGER_API_KEY || '');
const MAX_BODY_BYTES = 64 * 1024 * 1024;
const MAX_SINGLE_BINARY_BYTES = 16 * 1024 * 1024;
const MAX_CHUNK_BYTES = 16 * 1024 * 1024;
const DEFAULT_CHUNK_BYTES = 512 * 1024;
const LARGE_FILE_WARNING_BYTES = 10 * 1024 * 1024;
const TRANSFER_TTL_MS = 15 * 60 * 1000;
const RESERVED_WIN_NAMES = /^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?$/i;

function parseArgs(argv) {
  const result = {
    listen: '127.0.0.1',
    port: 8005,
    rootsFile: null
  };
  for (let i = 2; i < argv.length; i++) {
    const key = argv[i];
    const value = argv[i + 1];
    if (key === '--listen' && value) { result.listen = value; i++; }
    else if (key === '--port' && value) { result.port = Number(value); i++; }
    else if (key === '--roots-file' && value) { result.rootsFile = value; i++; }
  }
  return result;
}

const options = parseArgs(process.argv);
let allowedRoots = [];
const activeTransfers = new Map();

function normalizeRootPath(raw) {
  if (typeof raw !== 'string' || !raw.trim()) return null;
  try {
    const resolved = path.resolve(raw.trim());
    if (!fs.existsSync(resolved)) return null;
    const real = fs.realpathSync(resolved);
    const stat = fs.statSync(real);
    if (!stat.isDirectory()) return null;
    const parsed = path.parse(real);
    return real.length > parsed.root.length
      ? real.replace(/[\\/]+$/, '')
      : real;
  } catch (_) {
    return null;
  }
}

function setAllowedRoots(rawList) {
  const seen = new Set();
  const next = [];
  for (const item of Array.isArray(rawList) ? rawList : []) {
    const normalized = normalizeRootPath(item);
    if (!normalized) continue;
    const key = process.platform === 'win32' ? normalized.toLowerCase() : normalized;
    if (seen.has(key)) continue;
    seen.add(key);
    next.push(normalized);
  }
  allowedRoots = next;
}

function loadRootsFile(filePath) {
  if (!filePath || !fs.existsSync(filePath)) {
    allowedRoots = [];
    return;
  }
  try {
    const parsed = JSON.parse(fs.readFileSync(filePath, 'utf8'));
    if (Array.isArray(parsed)) setAllowedRoots(parsed);
    else if (parsed && Array.isArray(parsed.roots)) setAllowedRoots(parsed.roots);
    else allowedRoots = [];
  } catch (_) {
    allowedRoots = [];
  }
}

loadRootsFile(options.rootsFile);

function isManagerAuthorized(request) {
  if (!MANAGER_API_KEY) return false;
  const provided = String(request.headers['x-mcphotplug-manager-key'] || '');
  const expectedBuf = Buffer.from(MANAGER_API_KEY, 'utf8');
  const providedBuf = Buffer.from(provided, 'utf8');
  return expectedBuf.length === providedBuf.length &&
    crypto.timingSafeEqual(expectedBuf, providedBuf);
}

function isInsideRoot(targetPath, rootPath) {
  const isWin = process.platform === 'win32';
  const target = isWin ? targetPath.toLowerCase() : targetPath;
  const root = isWin ? rootPath.toLowerCase() : rootPath;
  if (target === root) return true;
  const prefix = root.endsWith(path.sep) ? root : root + path.sep;
  return target.startsWith(prefix);
}

function isAllowedRealPath(realPath) {
  return allowedRoots.some(root => isInsideRoot(realPath, root));
}

function assertSafeRawPath(rawPath) {
  if (typeof rawPath !== 'string' || !rawPath.trim()) {
    throw new Error('Path is required.');
  }
  const trimmed = rawPath.trim();
  if (trimmed.includes('\0')) {
    throw new Error('Path contains invalid null byte.');
  }
  if (trimmed.startsWith('\\\\') || trimmed.startsWith('//')) {
    throw new Error('UNC and Windows device paths are not allowed.');
  }
  if (!path.isAbsolute(trimmed)) {
    throw new Error('Absolute file path is required.');
  }
  if (process.platform === 'win32') {
    const afterDrive = /^[A-Za-z]:/.test(trimmed) ? trimmed.slice(2) : trimmed;
    if (afterDrive.includes(':')) {
      throw new Error('NTFS alternate data streams are not allowed.');
    }
    const segments = afterDrive.split(/[\\/]+/).filter(Boolean);
    for (const seg of segments) {
      if (RESERVED_WIN_NAMES.test(seg)) {
        throw new Error('Windows reserved device name is not allowed in path: ' + seg);
      }
    }
  }
  return trimmed;
}

function resolveReadFilePath(rawPath) {
  const trimmed = assertSafeRawPath(rawPath);
  if (allowedRoots.length === 0) {
    throw new Error('Access denied: no allowed directories are currently enabled in MCP Hotplug.');
  }
  const resolved = path.resolve(trimmed);
  if (!fs.existsSync(resolved)) {
    throw new Error('File not found: ' + resolved);
  }
  const realPath = fs.realpathSync(resolved);
  if (!isAllowedRealPath(realPath)) {
    throw new Error('Access denied: resolved path is outside allowed directories.');
  }
  const stat = fs.statSync(realPath);
  if (!stat.isFile()) {
    throw new Error('Target path is not a regular file.');
  }
  return { realPath, stat };
}

function resolveWriteFilePath(rawPath, createDirectories = true) {
  const trimmed = assertSafeRawPath(rawPath);
  if (allowedRoots.length === 0) {
    throw new Error('Access denied: no allowed directories are currently enabled in MCP Hotplug.');
  }
  const candidate = path.resolve(trimmed);
  let existed = false;
  try {
    const lstat = fs.lstatSync(candidate);
    existed = true;
    if (lstat.isSymbolicLink()) {
      throw new Error('Access denied: refusing to overwrite a symbolic link.');
    }
    if (!lstat.isFile()) {
      throw new Error('Target path already exists and is not a regular file.');
    }
  } catch (err) {
    if (!err || err.code !== 'ENOENT') throw err;
  }

  let cursor = path.dirname(candidate);
  const missingSegments = [];
  while (!fs.existsSync(cursor)) {
    const parent = path.dirname(cursor);
    if (parent === cursor) {
      throw new Error('Access denied: target drive or root directory does not exist.');
    }
    missingSegments.unshift(path.basename(cursor));
    cursor = parent;
  }

  const realExistingAncestor = fs.realpathSync(cursor);
  if (!isAllowedRealPath(realExistingAncestor)) {
    throw new Error('Access denied: target directory is outside allowed directories.');
  }

  const realTargetDirCandidate = missingSegments.length > 0
    ? path.join(realExistingAncestor, ...missingSegments)
    : realExistingAncestor;
  if (!isAllowedRealPath(realTargetDirCandidate)) {
    throw new Error('Access denied: target directory escapes allowed directories.');
  }

  if (missingSegments.length > 0) {
    if (!createDirectories) {
      throw new Error('Parent directory does not exist: ' + realTargetDirCandidate);
    }
    fs.mkdirSync(realTargetDirCandidate, { recursive: true });
  }

  const realDir = fs.realpathSync(realTargetDirCandidate);
  if (!isAllowedRealPath(realDir)) {
    throw new Error('Access denied: created directory resolved outside allowed directories.');
  }

  const finalPath = path.join(realDir, path.basename(candidate));
  if (!isAllowedRealPath(finalPath)) {
    throw new Error('Access denied: final target path is outside allowed directories.');
  }

  return { realPath: finalPath, realDir, existed };
}

function decodeBase64Strict(value) {
  if (typeof value !== 'string') {
    throw new Error('data_base64 must be a string.');
  }
  const cleaned = value.replace(/\s+/g, '');
  if (cleaned.length === 0) return Buffer.alloc(0);
  if (cleaned.length % 4 !== 0 || !/^[A-Za-z0-9+/]*={0,2}$/.test(cleaned)) {
    throw new Error('data_base64 is not valid Base64.');
  }
  return Buffer.from(cleaned, 'base64');
}

function sha256Buffer(buffer) {
  return crypto.createHash('sha256').update(buffer).digest('hex');
}

function sha256FileStream(filePath) {
  return new Promise((resolve, reject) => {
    const hash = crypto.createHash('sha256');
    const stream = fs.createReadStream(filePath);
    stream.on('data', chunk => hash.update(chunk));
    stream.on('end', () => resolve(hash.digest('hex')));
    stream.on('error', reject);
  });
}

function normalizeExpectedSha256(value) {
  if (value === undefined || value === null || value === '') return null;
  if (typeof value !== 'string') throw new Error('SHA-256 hash must be a hex string.');
  const clean = value.trim().toLowerCase();
  if (!/^[0-9a-f]{64}$/.test(clean)) {
    throw new Error('Invalid SHA-256 format (expected 64 hex characters).');
  }
  return clean;
}

function buildLargeFileWarning(sizeBytes) {
  if (!Number.isFinite(sizeBytes) || sizeBytes < LARGE_FILE_WARNING_BYTES) return null;
  const mb = (sizeBytes / (1024 * 1024)).toFixed(2);
  return 'WARNING: File size is ' + mb + ' MB (>= 10 MB). Processing very large binary files through the model context may fail or truncate the session; proceed carefully in chunks.';
}

function cleanupTransfer(transferId) {
  const transfer = activeTransfers.get(transferId);
  if (!transfer) return;
  activeTransfers.delete(transferId);
  try {
    if (transfer.tmpPath && fs.existsSync(transfer.tmpPath)) {
      fs.unlinkSync(transfer.tmpPath);
    }
  } catch (_) {}
}

setInterval(() => {
  const cutoff = Date.now() - TRANSFER_TTL_MS;
  for (const [id, transfer] of activeTransfers.entries()) {
    if (transfer.lastActivity < cutoff) cleanupTransfer(id);
  }
}, 60000).unref();

function atomicReplaceFromTemp(tmpPath, targetPath) {
  const fd = fs.openSync(tmpPath, 'r+');
  try { fs.fsyncSync(fd); }
  finally { fs.closeSync(fd); }
  fs.renameSync(tmpPath, targetPath);
}

async function toolReadBinary(args) {
  const { realPath, stat } = resolveReadFilePath(args.path);
  if (stat.size > MAX_SINGLE_BINARY_BYTES) {
    const mb = (stat.size / (1024 * 1024)).toFixed(2);
    throw new Error(
      'File size (' + mb + ' MB) exceeds the single-call read_binary limit (16 MB). Use read_chunk to read it in parts.'
    );
  }
  const buffer = await fs.promises.readFile(realPath);
  const sha256 = sha256Buffer(buffer);
  const warning = buildLargeFileWarning(buffer.length);
  return {
    path: realPath,
    size: buffer.length,
    sha256,
    encoding: 'base64',
    data_base64: buffer.toString('base64'),
    ...(warning ? { warning } : {})
  };
}

async function toolWriteBinary(args) {
  const buffer = decodeBase64Strict(args.data_base64);
  if (buffer.length > MAX_SINGLE_BINARY_BYTES) {
    const mb = (buffer.length / (1024 * 1024)).toFixed(2);
    throw new Error(
      'Payload size (' + mb + ' MB) exceeds the single-call write_binary limit (16 MB). Use write_chunk for larger files.'
    );
  }

  const expectedSha = normalizeExpectedSha256(args.expected_sha256);
  const actualSha = sha256Buffer(buffer);
  if (expectedSha && actualSha !== expectedSha) {
    throw new Error('SHA-256 mismatch before write. Expected ' + expectedSha + ', got ' + actualSha + '.');
  }

  const overwrite = args.overwrite !== false;
  const createDirectories = args.create_directories !== false;
  const { realPath, realDir, existed } = resolveWriteFilePath(args.path, createDirectories);
  if (existed && !overwrite) {
    throw new Error('File already exists and overwrite is set to false: ' + realPath);
  }

  const tmpPath = path.join(realDir, '.mcphotplug-bin-' + crypto.randomBytes(8).toString('hex') + '.tmp');
  try {
    const fd = fs.openSync(tmpPath, 'wx');
    try {
      if (buffer.length > 0) fs.writeSync(fd, buffer, 0, buffer.length, 0);
      fs.fsyncSync(fd);
    } finally {
      fs.closeSync(fd);
    }
    fs.renameSync(tmpPath, realPath);
  } catch (err) {
    try { if (fs.existsSync(tmpPath)) fs.unlinkSync(tmpPath); } catch (_) {}
    throw err;
  }

  const warning = buildLargeFileWarning(buffer.length);
  return {
    path: realPath,
    size: buffer.length,
    sha256: actualSha,
    overwritten: existed,
    ...(warning ? { warning } : {})
  };
}

async function toolReadChunk(args) {
  const { realPath, stat } = resolveReadFilePath(args.path);
  const offset = args.offset === undefined ? 0 : Number(args.offset);
  const length = args.length === undefined ? DEFAULT_CHUNK_BYTES : Number(args.length);

  if (!Number.isInteger(offset) || offset < 0) {
    throw new Error('offset must be a non-negative integer.');
  }
  if (!Number.isInteger(length) || length < 1 || length > MAX_CHUNK_BYTES) {
    throw new Error('length must be an integer between 1 and ' + MAX_CHUNK_BYTES + ' bytes.');
  }

  const totalSize = stat.size;
  const toRead = Math.min(length, Math.max(0, totalSize - offset));
  let buffer = Buffer.alloc(0);

  if (toRead > 0) {
    const handle = await fs.promises.open(realPath, 'r');
    try {
      buffer = Buffer.allocUnsafe(toRead);
      let totalRead = 0;
      while (totalRead < toRead) {
        const res = await handle.read(buffer, totalRead, toRead - totalRead, offset + totalRead);
        if (res.bytesRead === 0) break;
        totalRead += res.bytesRead;
      }
      if (totalRead < toRead) buffer = buffer.subarray(0, totalRead);
    } finally {
      await handle.close();
    }
  }

  const nextOffset = offset + buffer.length;
  const remainingBytes = Math.max(0, totalSize - nextOffset);
  const eof = remainingBytes === 0;
  const chunkSha = sha256Buffer(buffer);
  const warning = buildLargeFileWarning(totalSize);
  const result = {
    path: realPath,
    offset,
    bytes_read: buffer.length,
    total_size: totalSize,
    remaining_bytes: remainingBytes,
    next_offset: nextOffset,
    eof,
    chunk_sha256: chunkSha,
    encoding: 'base64',
    data_base64: buffer.toString('base64')
  };
  if (args.include_file_sha256 === true) {
    result.file_sha256 = await sha256FileStream(realPath);
  }
  if (warning) result.warning = warning;
  return result;
}

async function toolWriteChunk(args) {
  const transferIdArg = typeof args.transfer_id === 'string' && args.transfer_id.trim()
    ? args.transfer_id.trim()
    : null;

  if (args.abort === true) {
    if (!transferIdArg || !activeTransfers.has(transferIdArg)) {
      return { transfer_id: transferIdArg, aborted: false, message: 'No active transfer found for this transfer_id.' };
    }
    cleanupTransfer(transferIdArg);
    return { transfer_id: transferIdArg, aborted: true };
  }

  const chunkBuf = decodeBase64Strict(args.data_base64 === undefined ? '' : args.data_base64);
  if (chunkBuf.length > MAX_CHUNK_BYTES) {
    throw new Error('Chunk size exceeds maximum single-chunk limit (' + MAX_CHUNK_BYTES + ' bytes).');
  }

  const expectedChunkSha = normalizeExpectedSha256(args.chunk_sha256);
  const actualChunkSha = sha256Buffer(chunkBuf);
  if (expectedChunkSha && actualChunkSha !== expectedChunkSha) {
    throw new Error('Chunk SHA-256 mismatch. Expected ' + expectedChunkSha + ', got ' + actualChunkSha + '.');
  }

  const isFinal = args.is_final === true;
  const expectedFileSha = normalizeExpectedSha256(args.expected_file_sha256);
  const overwrite = args.overwrite !== false;
  const createDirectories = args.create_directories !== false;

  let transfer;
  if (transferIdArg) {
    transfer = activeTransfers.get(transferIdArg);
    if (!transfer) {
      throw new Error('Unknown or expired transfer_id: ' + transferIdArg);
    }
    if (args.path) {
      const check = resolveWriteFilePath(args.path, createDirectories);
      const a = process.platform === 'win32' ? check.realPath.toLowerCase() : check.realPath;
      const b = process.platform === 'win32' ? transfer.realPath.toLowerCase() : transfer.realPath;
      if (a !== b) {
        throw new Error('Path does not match the active transfer_id target.');
      }
    }
  } else {
    const { realPath, realDir, existed } = resolveWriteFilePath(args.path, createDirectories);
    if (existed && !overwrite) {
      throw new Error('File already exists and overwrite is set to false: ' + realPath);
    }
    const id = 'tx_' + crypto.randomBytes(10).toString('hex');
    const tmpPath = path.join(realDir, '.mcphotplug-chunk-' + id + '.tmp');
    const fd = fs.openSync(tmpPath, 'wx');
    fs.closeSync(fd);
    transfer = {
      id,
      realPath,
      realDir,
      tmpPath,
      existed,
      overwrite,
      writtenBytes: 0,
      hasher: crypto.createHash('sha256'),
      lastActivity: Date.now()
    };
    activeTransfers.set(id, transfer);
  }

  if (args.offset !== undefined) {
    const requestedOffset = Number(args.offset);
    if (!Number.isInteger(requestedOffset) || requestedOffset !== transfer.writtenBytes) {
      throw new Error(
        'Sequential chunk offset mismatch. Expected offset ' + transfer.writtenBytes + ', received ' + String(args.offset) + '.'
      );
    }
  }

  transfer.lastActivity = Date.now();
  if (chunkBuf.length > 0) {
    const handle = await fs.promises.open(transfer.tmpPath, 'r+');
    try {
      await handle.write(chunkBuf, 0, chunkBuf.length, transfer.writtenBytes);
    } finally {
      await handle.close();
    }
    transfer.writtenBytes += chunkBuf.length;
    transfer.hasher.update(chunkBuf);
  }

  const warning = buildLargeFileWarning(transfer.writtenBytes);

  if (!isFinal) {
    return {
      transfer_id: transfer.id,
      path: transfer.realPath,
      bytes_written: chunkBuf.length,
      total_written: transfer.writtenBytes,
      next_offset: transfer.writtenBytes,
      chunk_sha256: actualChunkSha,
      finalized: false,
      ...(warning ? { warning } : {})
    };
  }

  const finalSha = transfer.hasher.digest('hex');
  if (expectedFileSha && finalSha !== expectedFileSha) {
    cleanupTransfer(transfer.id);
    throw new Error('Final file SHA-256 mismatch. Expected ' + expectedFileSha + ', got ' + finalSha + '. Target file was not modified.');
  }

  if (!transfer.overwrite && fs.existsSync(transfer.realPath)) {
    cleanupTransfer(transfer.id);
    throw new Error('Target file was created concurrently and overwrite is false: ' + transfer.realPath);
  }

  try {
    atomicReplaceFromTemp(transfer.tmpPath, transfer.realPath);
    activeTransfers.delete(transfer.id);
  } catch (err) {
    cleanupTransfer(transfer.id);
    throw err;
  }

  return {
    transfer_id: transfer.id,
    path: transfer.realPath,
    bytes_written: chunkBuf.length,
    size: transfer.writtenBytes,
    chunk_sha256: actualChunkSha,
    file_sha256: finalSha,
    finalized: true,
    overwritten: transfer.existed,
    ...(warning ? { warning } : {})
  };
}

const TOOLS = [
  {
    name: 'read_binary',
    description:
      'Read a binary file from an allowed directory and return it as Base64 with size and SHA-256 checksum. ' +
      'Single-call limit is 16 MB; use read_chunk for larger files.',
    inputSchema: {
      type: 'object',
      properties: {
        path: { type: 'string', description: 'Absolute file path inside an allowed directory.' }
      },
      required: ['path'],
      additionalProperties: false
    }
  },
  {
    name: 'write_binary',
    description:
      'Atomically write a Base64-encoded binary file into an allowed directory with SHA-256 verification. ' +
      'Overwrites existing files safely via temp-file + atomic rename by default (pass overwrite: false to prevent overwriting).',
    inputSchema: {
      type: 'object',
      properties: {
        path: { type: 'string', description: 'Absolute target file path inside an allowed directory.' },
        data_base64: { type: 'string', description: 'Base64-encoded binary content.' },
        expected_sha256: { type: 'string', description: 'Optional expected lowercase/uppercase hex SHA-256 checksum.' },
        overwrite: { type: 'boolean', description: 'Allow atomic overwrite of an existing file (default: true).' },
        create_directories: { type: 'boolean', description: 'Create missing parent directories inside allowed roots (default: true).' }
      },
      required: ['path', 'data_base64'],
      additionalProperties: false
    }
  },
  {
    name: 'read_chunk',
    description:
      'Read a byte range (chunk) of any binary file inside an allowed directory as Base64 with chunk SHA-256. ' +
      'Supports files of arbitrary size; emits a warning when file size >= 10 MB.',
    inputSchema: {
      type: 'object',
      properties: {
        path: { type: 'string', description: 'Absolute file path inside an allowed directory.' },
        offset: { type: 'integer', minimum: 0, description: 'Starting byte offset (default: 0).' },
        length: { type: 'integer', minimum: 1, maximum: MAX_CHUNK_BYTES, description: 'Chunk length in bytes (default: 524288, max: 16777216).' },
        include_file_sha256: { type: 'boolean', description: 'Also compute and return full file SHA-256 (default: false).' }
      },
      required: ['path'],
      additionalProperties: false
    }
  },
  {
    name: 'write_chunk',
    description:
      'Write a binary file incrementally in chunks to a staged temp file, then atomically commit on is_final: true with SHA-256 verification. ' +
      'Omit transfer_id on the first chunk and pass the returned transfer_id on subsequent chunks. Supports files of arbitrary size.',
    inputSchema: {
      type: 'object',
      properties: {
        path: { type: 'string', description: 'Absolute target file path inside an allowed directory.' },
        data_base64: { type: 'string', description: 'Base64-encoded chunk data.' },
        transfer_id: { type: 'string', description: 'Transfer ID returned by the first write_chunk call.' },
        offset: { type: 'integer', minimum: 0, description: 'Optional expected byte offset for sequential validation.' },
        is_final: { type: 'boolean', description: 'Set true on the final chunk to verify and atomically commit the file.' },
        chunk_sha256: { type: 'string', description: 'Optional SHA-256 checksum of this chunk.' },
        expected_file_sha256: { type: 'string', description: 'Optional SHA-256 checksum of the complete file (verified on is_final: true).' },
        overwrite: { type: 'boolean', description: 'Allow atomic overwrite of an existing file (default: true).' },
        create_directories: { type: 'boolean', description: 'Create missing parent directories inside allowed roots (default: true).' },
        abort: { type: 'boolean', description: 'Set true with transfer_id to cancel and delete the staged temp file.' }
      },
      required: ['path'],
      additionalProperties: false
    }
  }
];

function readRequestBody(request) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let total = 0;
    request.on('data', chunk => {
      total += chunk.length;
      if (total > MAX_BODY_BYTES) {
        reject(Object.assign(new Error('Payload Too Large'), { statusCode: 413 }));
        request.destroy();
        return;
      }
      chunks.push(chunk);
    });
    request.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    request.on('error', reject);
  });
}

function sendJson(response, statusCode, payload, headers = {}) {
  response.writeHead(statusCode, {
    'Content-Type': 'application/json; charset=utf-8',
    'Cache-Control': 'no-store',
    ...headers
  });
  response.end(JSON.stringify(payload));
}

async function handleToolCall(params) {
  const name = String((params && params.name) || '');
  const args = params && params.arguments && typeof params.arguments === 'object' && !Array.isArray(params.arguments)
    ? params.arguments
    : {};

  let data;
  if (name === 'read_binary') data = await toolReadBinary(args);
  else if (name === 'write_binary') data = await toolWriteBinary(args);
  else if (name === 'read_chunk') data = await toolReadChunk(args);
  else if (name === 'write_chunk') data = await toolWriteChunk(args);
  else {
    return {
      isError: true,
      content: [{ type: 'text', text: 'Unknown Binary MCP tool: ' + name }]
    };
  }

  return {
    content: [{ type: 'text', text: JSON.stringify(data) }],
    structuredContent: data
  };
}

const server = http.createServer(async (request, response) => {
  try {
    const reqUrl = new URL(request.url || '/', 'http://' + (request.headers.host || '127.0.0.1'));

    if (request.method === 'GET' && reqUrl.pathname === '/__manager/health') {
      sendJson(response, 200, { status: 'ok', service: 'Binary MCP', rootsCount: allowedRoots.length });
      return;
    }

    if (request.method === 'POST' && reqUrl.pathname === '/__manager/roots') {
      if (!isManagerAuthorized(request)) {
        sendJson(response, 404, { error: 'Not found.' });
        return;
      }
      const raw = await readRequestBody(request);
      const parsed = raw ? JSON.parse(raw) : {};
      setAllowedRoots(Array.isArray(parsed.roots) ? parsed.roots : []);
      sendJson(response, 200, { ok: true, rootsCount: allowedRoots.length });
      return;
    }

    if (reqUrl.pathname !== '/mcp') {
      sendJson(response, 404, { error: 'Not found.' });
      return;
    }

    if (request.method === 'DELETE') {
      sendJson(response, 200, { ok: true });
      return;
    }

    if (request.method === 'GET') {
      response.writeHead(405, { Allow: 'POST, DELETE', 'Cache-Control': 'no-store' });
      response.end();
      return;
    }

    if (request.method !== 'POST') {
      response.writeHead(405, { Allow: 'POST, DELETE' });
      response.end();
      return;
    }

    const rawBody = await readRequestBody(request);
    let rpc;
    try { rpc = JSON.parse(rawBody); }
    catch (_) {
      sendJson(response, 400, { jsonrpc: '2.0', id: null, error: { code: -32700, message: 'Parse error.' } });
      return;
    }

    if (!rpc || rpc.jsonrpc !== '2.0' || typeof rpc.method !== 'string') {
      sendJson(response, 400, { jsonrpc: '2.0', id: rpc && rpc.id !== undefined ? rpc.id : null, error: { code: -32600, message: 'Invalid Request.' } });
      return;
    }

    if (rpc.id === undefined || rpc.id === null) {
      response.writeHead(202, { 'Cache-Control': 'no-store' });
      response.end();
      return;
    }

    const sessionId = String(request.headers['mcp-session-id'] || 'binary-mcp-session');
    let result;

    if (rpc.method === 'initialize') {
      result = {
        protocolVersion: PROTOCOL_VERSION,
        capabilities: { tools: {} },
        serverInfo: SERVER_INFO
      };
    } else if (rpc.method === 'ping') {
      result = {};
    } else if (rpc.method === 'tools/list') {
      result = { tools: TOOLS };
    } else if (rpc.method === 'tools/call') {
      try {
        result = await handleToolCall(rpc.params || {});
      } catch (err) {
        result = {
          isError: true,
          content: [{ type: 'text', text: err instanceof Error ? err.message : String(err) }]
        };
      }
    } else {
      sendJson(response, 200, {
        jsonrpc: '2.0',
        id: rpc.id,
        error: { code: -32601, message: 'Method not found: ' + rpc.method }
      }, { 'Mcp-Session-Id': sessionId });
      return;
    }

    sendJson(response, 200, { jsonrpc: '2.0', id: rpc.id, result }, { 'Mcp-Session-Id': sessionId });
  } catch (err) {
    const status = err && err.statusCode ? err.statusCode : 500;
    if (!response.headersSent) {
      sendJson(response, status, { error: err instanceof Error ? err.message : String(err) });
    } else {
      response.destroy();
    }
  }
});

function shutdown() {
  for (const id of [...activeTransfers.keys()]) cleanupTransfer(id);
  server.close(() => process.exit(0));
}

process.once('SIGTERM', shutdown);
process.once('SIGINT', shutdown);

server.listen(options.port, '127.0.0.1');