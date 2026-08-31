// 生成凌溪工具箱图标（PNG）：蓝色渐变圆角方块 + 玻璃高光带。
// 用法：node scripts/make-icon.mjs <输出路径>
import fs from 'node:fs';
import zlib from 'node:zlib';

function crc32(buf) {
  const table = [];
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c >>> 0;
  }
  let c = 0xffffffff;
  for (const b of buf) c = table[(c ^ b) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const t = Buffer.from(type, 'ascii');
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(Buffer.concat([t, data])));
  return Buffer.concat([len, t, data, crc]);
}

function encodePng(w, h, px) {
  const sig = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(w, 0);
  ihdr.writeUInt32BE(h, 4);
  ihdr[8] = 8;  // bit depth
  ihdr[9] = 6;  // RGBA
  const raw = Buffer.alloc((w * 4 + 1) * h);
  for (let y = 0; y < h; y++) {
    raw[y * (w * 4 + 1)] = 0;
    px.copy(raw, y * (w * 4 + 1) + 1, y * w * 4, (y + 1) * w * 4);
  }
  return Buffer.concat([sig, chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
}

// 圆角矩形内正距离（边缘处 0）
function roundedRectDist(x, y, w, h, r) {
  const dx = Math.abs(x + 0.5 - w / 2) - (w / 2 - r);
  const dy = Math.abs(y + 0.5 - h / 2) - (h / 2 - r);
  const ox = Math.max(dx, 0);
  const oy = Math.max(dy, 0);
  return Math.min(Math.max(dx, dy), 0) + Math.hypot(ox, oy) - r;
}

const S = 256;
const R = 58;
const px = Buffer.alloc(S * S * 4);
for (let y = 0; y < S; y++) {
  for (let x = 0; x < S; x++) {
    const d = roundedRectDist(x, y, S, S, R);
    const inside = Math.min(1, Math.max(0, -d)); // 1=内
    const i = (y * S + x) * 4;

    // 基础渐变 #0A84FF → #0050C8（对角）
    const t = (x + y) / (2 * S);
    let r = Math.round(0x0a + (0x00 - 0x0a) * t);
    let g = Math.round(0x84 + (0x50 - 0x84) * t);
    let b = Math.round(0xff + (0xc8 - 0xff) * t);

    // 顶部玻璃高光带（28% 白渐隐）
    const hl = Math.max(0, 1 - y / (S * 0.5)) * 0.28;
    // 边缘内侧 1.5px 白描边高光（模拟玻璃厚度）
    const ring = Math.max(0, 1 - Math.abs(-d - 1.5) / 1.5) * 0.5;

    r = Math.min(255, Math.round(r + 255 * hl + 255 * ring));
    g = Math.min(255, Math.round(g + 255 * hl + 255 * ring));
    b = Math.min(255, Math.round(b + 255 * hl + 255 * ring));

    px[i] = r;
    px[i + 1] = g;
    px[i + 2] = b;
    px[i + 3] = Math.round(inside * 255);
  }
}

const out = process.argv[2] || 'app.png';
fs.writeFileSync(out, encodePng(S, S, px));
console.log('icon written:', out);
