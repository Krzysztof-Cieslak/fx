// Minimal ZIP reader: enough to pull one file out of a Bun release archive without relying on
// `unzip` or `tar` being installed (they differ across Linux, macOS and Windows). The real
// component installer (T110) extracts in F# with System.IO.Compression.

import { inflateRawSync } from "node:zlib";

const EOCD_SIGNATURE = 0x06054b50;
const CENTRAL_SIGNATURE = 0x02014b50;
const LOCAL_SIGNATURE = 0x04034b50;

export interface ZipEntry {
  name: string;
  method: number;
  crc32: number;
  compressedSize: number;
  size: number;
  localHeaderOffset: number;
}

function findEndOfCentralDirectory(view: DataView): number {
  // The comment at the end is at most 64 KiB.
  const min = Math.max(0, view.byteLength - 22 - 0xffff);
  for (let i = view.byteLength - 22; i >= min; i--) {
    if (view.getUint32(i, true) === EOCD_SIGNATURE) return i;
  }
  throw new Error("zip: end of central directory not found");
}

export function listEntries(zip: Uint8Array): ZipEntry[] {
  const view = new DataView(zip.buffer, zip.byteOffset, zip.byteLength);
  const eocd = findEndOfCentralDirectory(view);
  const count = view.getUint16(eocd + 10, true);
  let offset = view.getUint32(eocd + 16, true);
  const decoder = new TextDecoder();
  const entries: ZipEntry[] = [];
  for (let i = 0; i < count; i++) {
    if (view.getUint32(offset, true) !== CENTRAL_SIGNATURE) throw new Error("zip: bad central directory");
    const nameLength = view.getUint16(offset + 28, true);
    const extraLength = view.getUint16(offset + 30, true);
    const commentLength = view.getUint16(offset + 32, true);
    entries.push({
      method: view.getUint16(offset + 10, true),
      crc32: view.getUint32(offset + 16, true),
      compressedSize: view.getUint32(offset + 20, true),
      size: view.getUint32(offset + 24, true),
      localHeaderOffset: view.getUint32(offset + 42, true),
      name: decoder.decode(zip.subarray(offset + 46, offset + 46 + nameLength)),
    });
    offset += 46 + nameLength + extraLength + commentLength;
  }
  return entries;
}

/** Extracts one entry and verifies its size and CRC-32. */
export function extractEntry(zip: Uint8Array, name: string): Uint8Array {
  const entry = listEntries(zip).find((e) => e.name === name);
  if (!entry) throw new Error(`zip: entry not found: ${name}`);
  const view = new DataView(zip.buffer, zip.byteOffset, zip.byteLength);
  const at = entry.localHeaderOffset;
  if (view.getUint32(at, true) !== LOCAL_SIGNATURE) throw new Error("zip: bad local header");
  const dataStart = at + 30 + view.getUint16(at + 26, true) + view.getUint16(at + 28, true);
  const raw = zip.subarray(dataStart, dataStart + entry.compressedSize);
  let data: Uint8Array;
  if (entry.method === 0) data = raw;
  else if (entry.method === 8) data = inflateRawSync(raw);
  else throw new Error(`zip: unsupported compression method ${entry.method}`);
  if (data.byteLength !== entry.size) throw new Error(`zip: size mismatch for ${name}`);
  if (Bun.hash.crc32(data) !== entry.crc32) throw new Error(`zip: CRC-32 mismatch for ${name}`);
  return data;
}
