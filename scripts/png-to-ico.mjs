// 把 256x256 RGBA PNG 转成传统 BMP 帧 ICO（.NET Win32 资源器接受的标准格式）。
// 用法：node scripts/png-to-ico.mjs <输入.png> <输出.ico>
import fs from 'node:fs';
import zlib from 'node:zlib';

const [, , inPath, outPath] = process.argv;
const png = fs.readFileSync(inPath);

// —— 最小 PNG 解码（8bit RGBA/RGB，支持 filter 0-4）——
const sig = png.subarray(0, 8);
let pos = 8;
let w = 0, h = 0, bitDepth = 0, colorType = 0;
const idat = [];
while (pos < png.length) {
  const len = png.readUInt32BE(pos);
  const type = png.subarray(pos + 4, pos + 8).toString('ascii');
  const data = png.subarray(pos + 8, pos + 8 + len);
  if (type === 'IHDR') {
    w = data.readUInt32BE(0); h = data.readUInt32BE(4);
    bitDepth = data[8]; colorType = data[9];
  } else if (type === 'IDAT') {
    idat.push(data);
  } else if (type === 'IEND') break;
  pos += 12 + len;
}
if (bitDepth !== 8 || (colorType !== 6 && colorType !== 2)) {
  throw new Error(`unsupported PNG: depth=${bitDepth} color=${colorType}`);
}
const channels = colorType === 6 ? 4 : 3;
const raw = zlib.inflateSync(Buffer.concat(idat));
const stride = w * channels;
const px = Buffer.alloc(w * h * 4); // RGBA

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
    let r, g, b, a = 255;
    const left = x > 0 ? px.subarray(out - 4, out) : null;
    const read = (off) => {
      let v = line[i + off];
      const xl = x > 0 ? px[out - 4 + off] : 0;
      const yu = y > 0 ? px[out - w * 4 + off] : 0;
      const xlu = x > 0 && y > 0 ? px[out - w * 4 - 4 + off] : 0;
      if (f === 1) v = (v + xl) & 0xff;
      else if (f === 2) v = (v + yu) & 0xff;
      else if (f === 3) v = (v + ((xl + yu) >> 1)) & 0xff;
      else if (f === 4) v = (v + paeth(xl, yu, xlu)) & 0xff;
      return v;
    };
    r = read(0); g = read(1); b = read(2);
    if (channels === 4) a = read(3);
    px[out++] = r; px[out++] = g; px[out++] = b; px[out++] = a;
  }
}

// —— RGBA → 32bpp BMP（自下而上 BGRA）+ 空 AND mask ——
const bmpHeader = Buffer.alloc(40);
bmpHeader.writeUInt32LE(40, 0);          // biSize
bmpHeader.writeInt32LE(w, 4);
bmpHeader.writeInt32LE(h * 2, 8);        // XOR + AND
bmpHeader.writeUInt16LE(1, 12);          // planes
bmpHeader.writeUInt16LE(32, 14);         // bpp
bmpHeader.writeUInt32LE(0, 16);          // BI_RGB
bmpHeader.writeUInt32LE(w * h * 4, 20);  // biSizeImage

const xor = Buffer.alloc(w * h * 4);
for (let y = 0; y < h; y++) {
  for (let x = 0; x < w; x++) {
    const si = (y * w + x) * 4, di = ((h - 1 - y) * w + x) * 4;
    xor[di] = px[si + 2];     // B
    xor[di + 1] = px[si + 1]; // G
    xor[di + 2] = px[si];     // R
    xor[di + 3] = px[si + 3]; // A
  }
}
const maskRow = Math.ceil(w / 32) * 4;
const and = Buffer.alloc(maskRow * h);

const image = Buffer.concat([bmpHeader, xor, and]);
const head = Buffer.alloc(6);
head[2] = 1;
const entry = Buffer.alloc(16);
entry[0] = 0; entry[1] = 0;      // 256
entry.writeUInt16LE(1, 4);
entry.writeUInt16LE(32, 6);
entry.writeUInt32LE(image.length, 8);
entry.writeUInt32LE(22, 12);

fs.writeFileSync(outPath, Buffer.concat([head, entry, image]));
console.log(`ico written: ${outPath} (${w}x${h}, ${image.length} bytes)`);
