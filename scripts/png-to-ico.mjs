// 256x256 RGBA PNG → 多尺寸 BMP 帧 ICO（16/32/48/256，正确帧表）。
// 上一版 bug：ICONDIR 的 count 从未写入（Buffer.alloc 默认 0）→ "帧数 0" →
// 托盘空 hicon + CS7065（.NET 资源器拒收）。本版修复并支持缩放多帧。
// 用法：node scripts/png-to-ico.mjs <输入.png> <输出.ico>
import fs from 'node:fs';
import zlib from 'node:zlib';

const [, , inPath, outPath] = process.argv;
const png = fs.readFileSync(inPath);

// —— 最小 PNG 解码（8bit RGBA/RGB，filter 0-4）——
let pos = 8, w = 0, h = 0, bitDepth = 0, colorType = 0;
const idat = [];
while (pos < png.length) {
  const len = png.readUInt32BE(pos);
  const type = png.subarray(pos + 4, pos + 8).toString('ascii');
  const data = png.subarray(pos + 8, pos + 8 + len);
  if (type === 'IHDR') {
    w = data.readUInt32BE(0); h = data.readUInt32BE(4);
    bitDepth = data[8]; colorType = data[9];
  } else if (type === 'IDAT') idat.push(data);
  else if (type === 'IEND') break;
  pos += 12 + len;
}
if (bitDepth !== 8 || (colorType !== 6 && colorType !== 2)) {
  throw new Error(`unsupported PNG: depth=${bitDepth} color=${colorType}`);
}
const channels = colorType === 6 ? 4 : 3;
const raw = zlib.inflateSync(Buffer.concat(idat));
const stride = w * channels;
const src = Buffer.alloc(w * h * 4);

const paeth = (a, b, c) => {
  const p = a + b - c, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c);
  return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
};

let out = 0;
for (let y = 0; y < h; y++) {
  const f = raw[y * (stride + 1)];
  const line = raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1));
  for (let x = 0; x < w; x++) {
    const i = x * channels;
    const read = (off) => {
      let v = line[i + off];
      const xl = x > 0 ? src[out - 4 + off] : 0;
      const yu = y > 0 ? src[out - w * 4 + off] : 0;
      const xlu = x > 0 && y > 0 ? src[out - w * 4 - 4 + off] : 0;
      if (f === 1) v = (v + xl) & 0xff;
      else if (f === 2) v = (v + yu) & 0xff;
      else if (f === 3) v = (v + ((xl + yu) >> 1)) & 0xff;
      else if (f === 4) v = (v + paeth(xl, yu, xlu)) & 0xff;
      return v;
    };
    src[out++] = read(0); src[out++] = read(1); src[out++] = read(2);
    src[out++] = channels === 4 ? read(3) : 255;
  }
}

// —— 双线性缩放（高质量，对图标足够）——
function scale(px, sw, sh, dw, dh) {
  if (sw === dw && sh === dh) return px;
  const dst = Buffer.alloc(dw * dh * 4);
  for (let y = 0; y < dh; y++) {
    const sy = (y + 0.5) * sh / dh - 0.5;
    const y0 = Math.max(0, Math.floor(sy)), y1 = Math.min(sh - 1, y0 + 1);
    const fy = Math.max(0, Math.min(1, sy - y0));
    for (let x = 0; x < dw; x++) {
      const sx = (x + 0.5) * sw / dw - 0.5;
      const x0 = Math.max(0, Math.floor(sx)), x1 = Math.min(sw - 1, x0 + 1);
      const fx = Math.max(0, Math.min(1, sx - x0));
      const di = (y * dw + x) * 4;
      for (let c = 0; c < 4; c++) {
        const p00 = src[(y0 * sw + x0) * 4 + c], p10 = src[(y0 * sw + x1) * 4 + c];
        const p01 = src[(y1 * sw + x0) * 4 + c], p11 = src[(y1 * sw + x1) * 4 + c];
        dst[di + c] = Math.round(
          p00 * (1 - fx) * (1 - fy) + p10 * fx * (1 - fy) +
          p01 * (1 - fx) * fy + p11 * fx * fy);
      }
    }
  }
  return dst;
}

// —— RGBA → 32bpp BMP 帧（自下而上 BGRA + 空 AND mask）——
function bmpFrame(px, fw, fh) {
  const head = Buffer.alloc(40);
  head.writeUInt32LE(40, 0);
  head.writeInt32LE(fw, 4);
  head.writeInt32LE(fh * 2, 8);
  head.writeUInt16LE(1, 12);
  head.writeUInt16LE(32, 14);
  head.writeUInt32LE(fw * fh * 4, 20);
  const xor = Buffer.alloc(fw * fh * 4);
  for (let y = 0; y < fh; y++) {
    for (let x = 0; x < fw; x++) {
      const si = (y * fw + x) * 4, di = ((fh - 1 - y) * fw + x) * 4;
      xor[di] = px[si + 2]; xor[di + 1] = px[si + 1]; xor[di + 2] = px[si]; xor[di + 3] = px[si + 3];
    }
  }
  const maskRow = Math.ceil(fw / 32) * 4;
  return Buffer.concat([head, xor, Buffer.alloc(maskRow * fh)]);
}

// —— 组装多帧 ICO（ICONDIR 帧表必须写 count！）——
const sizes = [16, 32, 48, 256].filter(s => s <= Math.max(w, h));
const frames = sizes.map(s => ({ size: s, data: bmpFrame(scale(src, w, h, s, s), s, s) }));

const dir = Buffer.alloc(6 + frames.length * 16);
dir.writeUInt16LE(0, 0);   // reserved
dir.writeUInt16LE(1, 2);   // type = icon
dir.writeUInt16LE(frames.length, 4); // ★ count（上版漏写 → 帧数 0）

let offset = 6 + frames.length * 16;
const blobs = [];
frames.forEach((f, i) => {
  const e = 6 + i * 16;
  dir[e] = f.size >= 256 ? 0 : f.size;      // 宽（256 用 0）
  dir[e + 1] = f.size >= 256 ? 0 : f.size; // 高
  dir[e + 2] = 0; dir[e + 3] = 0;          // colorCount / reserved
  dir.writeUInt16LE(1, e + 4);             // planes
  dir.writeUInt16LE(32, e + 6);            // bpp
  dir.writeUInt32LE(f.data.length, e + 8);
  dir.writeUInt32LE(offset, e + 12);
  blobs.push(f.data);
  offset += f.data.length;
});

fs.writeFileSync(outPath, Buffer.concat([dir, ...blobs]));
console.log(`ico written: ${outPath} frames=[${sizes.join(',')}] total=${offset}B`);
