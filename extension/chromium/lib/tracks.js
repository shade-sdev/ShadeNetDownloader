// What is inside a stream's pieces: picture, sound, or both. A media playlist does not say (it only lists pieces), and a
// player that asks for the picture's playlist and the sound's playlist separately leaves two entries that belong together.
// The first bytes of the first piece do say: the initialisation part of an MP4 names its tracks ("vide", "soun"), an MPEG-TS
// stream lists its elementary streams in its program table, and a bare AAC or MP3 file is sound.
//
//   inspectMedia(bytes) -> { kind: 'video' | 'audio' | 'both' | 'unknown', width, height }
//
// Pure functions over bytes; the browser calls them with the first 64 KB of a piece.

const text = (b, at, length) => String.fromCharCode(...b.subarray(at, at + length));
const u16 = (b, at) => (b[at] << 8) | b[at + 1];

const VIDEO_ENTRIES = ['avc1', 'avc3', 'hvc1', 'hev1', 'vp09', 'av01', 'encv', 'dvh1', 'dvhe'];

/** Handler types ("vide", "soun") and the picture's size from the sample entry, found by scanning the boxes' names. */
// boxes that only an encrypted (protected) stream has: its sample entries, the scheme it uses, the licence system's data
const ENCRYPTION_BOXES = ['encv', 'enca', 'sinf', 'tenc', 'pssh'];

function inspectIsoBmff(b) {
  let video = false;
  let audio = false;
  let width = 0;
  let height = 0;
  let encrypted = false;

  for (let i = 4; i + 16 <= b.length; i++) {
    if (b[i] === 0x68 && text(b, i, 4) === 'hdlr') {            // box: size, 'hdlr', version/flags, pre_defined, handler_type
      const handler = text(b, i + 12, 4);
      if (handler === 'vide') video = true;
      else if (handler === 'soun') audio = true;
    }
  }

  for (let i = 4; i + 4 <= b.length && !encrypted; i++) {
    if ([0x65, 0x73, 0x74, 0x70].includes(b[i]) && ENCRYPTION_BOXES.includes(text(b, i, 4))) encrypted = true;   // e, s, t, p: cheap first look
  }

  // The sample entries live in the sample description ('stsd'). The same names appear elsewhere, among the brands of 'ftyp' for one
  // ("avc1" is a brand too), where the bytes after them are no picture size: only look after 'stsd'.
  let from = 4;
  for (let i = 4; i + 4 <= b.length; i++) {
    if (b[i] === 0x73 && text(b, i, 4) === 'stsd') {
      from = i + 4;
      break;
    }
  }

  for (const entry of VIDEO_ENTRIES) {
    for (let i = from; i + 32 <= b.length; i++) {
      if (b[i] === entry.charCodeAt(0) && text(b, i, 4) === entry) {   // VisualSampleEntry: width and height 28 bytes after the name
        width = u16(b, i + 28);
        height = u16(b, i + 30);
        video = true;
        break;
      }
    }
    if (height) break;
  }

  return { kind: video && audio ? 'both' : video ? 'video' : audio ? 'audio' : 'unknown', width, height, encrypted };
}

// stream types of an MPEG-TS program (ISO 13818-1 table 2-34 and the usual extensions)
const TS_VIDEO = new Set([0x01, 0x02, 0x10, 0x1b, 0x24, 0x42]);
const TS_AUDIO = new Set([0x03, 0x04, 0x0f, 0x11, 0x81, 0x87]);

function inspectTransportStream(b) {
  let pmtPid = -1;
  const types = [];

  for (let at = 0; at + 188 <= b.length; at += 188) {
    if (b[at] !== 0x47) return null;
    const startsSection = (b[at + 1] & 0x40) !== 0;
    const pid = ((b[at + 1] & 0x1f) << 8) | b[at + 2];
    const control = (b[at + 3] >> 4) & 3;
    if (!(control & 1) || !startsSection) continue;

    let p = at + 4;
    if (control & 2) p += 1 + b[p];                // adaptation field
    p += 1 + b[p];                                  // pointer field
    if (p + 12 > at + 188) continue;

    if (pid === 0 && b[p] === 0x00) {              // program association: which PID carries the program map
      const end = Math.min(p + 3 + (((b[p + 1] & 0x0f) << 8) | b[p + 2]) - 4, at + 188);
      for (let q = p + 8; q + 4 <= end; q += 4) {
        if (u16(b, q) !== 0) { pmtPid = ((b[q + 2] & 0x1f) << 8) | b[q + 3]; break; }
      }
    } else if (pid === pmtPid && b[p] === 0x02) {  // program map: the elementary streams
      const end = Math.min(p + 3 + (((b[p + 1] & 0x0f) << 8) | b[p + 2]) - 4, at + 188);
      let q = p + 12 + (((b[p + 10] & 0x0f) << 8) | b[p + 11]);
      while (q + 5 <= end) {
        types.push(b[q]);
        q += 5 + (((b[q + 3] & 0x0f) << 8) | b[q + 4]);
      }
    }
  }

  if (types.length === 0) return null;
  const video = types.some((t) => TS_VIDEO.has(t));
  const audio = types.some((t) => TS_AUDIO.has(t));
  return { kind: video && audio ? 'both' : video ? 'video' : audio ? 'audio' : 'unknown', width: 0, height: 0 };
}

/** Is this the first bytes of something that is only sound (a bare AAC, MP3 or AC-3 stream, possibly after an ID3 tag)? */
function isBareSound(b) {
  if (b.length < 4) return false;
  if (b[0] === 0x49 && b[1] === 0x44 && b[2] === 0x33) return true;                       // "ID3": tagged AAC/MP3
  if (b[0] === 0xff && (b[1] & 0xf6) === 0xf0) return true;                              // ADTS (AAC)
  if (b[0] === 0xff && (b[1] & 0xe0) === 0xe0 && (b[1] & 0x18) !== 0x08) return true;    // an MPEG audio frame (MP3)
  return b[0] === 0x0b && b[1] === 0x77;                                                  // AC-3
}

export function inspectMedia(bytes) {
  const b = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes ?? []);
  const none = { kind: 'unknown', width: 0, height: 0 };
  if (b.length < 8) return none;

  if (b[0] === 0x47 && (b.length < 189 || b[188] === 0x47)) return inspectTransportStream(b) ?? none;
  if (['ftyp', 'styp', 'moov', 'moof', 'free', 'mdat'].includes(text(b, 4, 4))) return inspectIsoBmff(b);
  if (isBareSound(b)) return { kind: 'audio', width: 0, height: 0 };
  return none;
}
