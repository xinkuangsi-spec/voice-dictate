// Chinese inverse text normalization: spoken numbers to digits ("十六分钟" -> "16分钟").
// Port of chinese_itn.py from CapsWriter-Offline (MIT, https://github.com/HaujetZhao/CapsWriter-Offline),
// which uses it for the same Qwen3-ASR output. Behaviour matches the Python original line for line,
// plus one rule: the adverb "十分" (very) is left alone unless "钟" follows.

const unitMapping = {
  "个": null, "只": null, "分": null, "万": null, "亿": null, "秒": null, "年": null,
  "月": null, "日": null, "天": null, "时": null, "钟": null, "人": null, "层": null,
  "楼": null, "倍": null, "块": null, "次": null,
  "克": "g", "千克": "kg",
  "米": "米", "千米": "千米", "千米每小时": "km/h",
};
const sortedUnits = Object.keys(unitMapping).sort((a, b) => b.length - a.length);
const commonUnits = sortedUnits.join("|");

const numMapper = { "零": "0", "一": "1", "幺": "1", "二": "2", "两": "2", "三": "3", "四": "4", "五": "5", "六": "6", "七": "7", "八": "8", "九": "9", "点": "." };
const valueMapper = { "零": 0, "一": 1, "二": 2, "两": 2, "三": 3, "四": 4, "五": 5, "六": 6, "七": 7, "八": 8, "九": 9, "十": 10, "百": 100, "千": 1000, "万": 10000, "亿": 100000000 };

const idioms = `
正经八百  五零二落 五零四散
五十步笑百步 乌七八糟 污七八糟 四百四病 思绪万千
十有八九 十之八九 三十而立 三十六策 三十六计 三十六行
三五成群 三百六十行 三六九等
七老八十 七零八落 七零八碎 七七八八 乱七八遭 乱七八糟 略知一二 零零星星 零七八碎
九九归一 二三其德 二三其意 无银三百两 八九不离十
百分之百 年三十 烂七八糟 一点一滴 路易十六 九三学社 五四运动 入木三分 三十六计
九九八十一 三七二十一
十二五 十三五 十四五 十五五 十六五 十七五 十八五
`.split(/\s+/).filter(Boolean);

const fuzzyRegex = /几/;

// ---- ranges: 三五百 -> 300~500, 十五六 -> 15~16 ----

const digit = (c) => valueMapper[c] || 0;
const parseTens = (tens) => (tens === "十" ? 10 : digit(tens[0]) * 10);

const rangePattern1 = /([二三四五六七八九])([二三四五六七八九])([十百千万亿])([万千百亿])?/;
const rangePattern2 = /(十|[一二三四五六七八九十]+[十百千万])([一二三四五六七八九])([一二三四五六七八九])([万千亿])?/;
const rangePattern3 = /^([一二三四五六七八九])([一二三四五六七八九])$/;

function convertRange1(m) {
  const [, d1, d2, unit] = m;
  const suffix = m[4] || "";
  let v1 = digit(d1), v2 = digit(d2);
  if (unit === "十") return `${v1 * 10}~${v2 * 10}${suffix}`;
  if (unit === "万" || unit === "亿") return `${v1}~${v2}${unit}${suffix}`;
  if (unit === "千" && suffix) return `${v1}~${v2}${unit}${suffix}`;
  return `${v1 * valueMapper[unit]}~${v2 * valueMapper[unit]}${suffix}`;
}

function convertRange2(m) {
  const [, base, d1, d2] = m;
  const unit = m[4] || "";
  const last = base[base.length - 1];
  let baseValue;
  if (last === "十") baseValue = base.length === 1 ? 10 : digit(base[0]) * 10;
  else if (last in valueMapper) {
    const numPart = base.slice(0, -1);
    baseValue = numPart ? digit(numPart[0]) * valueMapper[last] : valueMapper[last];
  } else baseValue = parseTens(base);
  const mult = Math.floor((valueMapper[last] || 10) / 10);
  return `${baseValue + digit(d1) * mult}~${baseValue + digit(d2) * mult}${unit}`;
}

const convertRange3 = (m) => `${digit(m[1])}~${digit(m[2])}`;

const escapeRe = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
const optionalUnit = `(?:${sortedUnits.map(escapeRe).join("|")})?`;
const rangeDetect = new RegExp(
  "(?<!点)(?:" +
    `[二三四五六七八九]{2}(?:十|[百千万亿])${optionalUnit}` +
    `|[一二三四五六七八九]?十[一二三四五六七八九]{2}(?:[万千亿]|${optionalUnit})` +
    "|[一二三四五六七八九][百千][二三四五六七八九]{2}十" +
    `|[一二三四五六七八九十]+[万千百][一二三四五六七八九]{2}${optionalUnit}` +
  ")");

function convertRangeExpression(text) {
  let stripped = text, mapped = "";
  for (const u of sortedUnits) {
    if ("万亿千百十".includes(u)) continue;
    if (text.endsWith(u)) {
      stripped = text.slice(0, -u.length);
      mapped = unitMapping[u] === null ? u : unitMapping[u];
      break;
    }
  }
  let m = rangePattern2.exec(stripped);
  if (m) return convertRange2(m) + mapped;
  m = rangePattern1.exec(stripped);
  if (m) return convertRange1(m) + mapped;
  m = rangePattern3.exec(stripped);
  if (m) return convertRange3(m) + mapped;
  return text;
}

// ---- patterns ----

const unitSuffix = new RegExp(`(${commonUnits}|[a-zA-Z]+)$`);

// Python's pattern uses conditional groups ((?(1)...)), which JS lacks. At each length of the digit run it
// tries a unit first, then (without a leading letter) a further digit tail — the alternation below keeps that order.
const G3 = "(?:[几零幺一二两三四五六七八九十百千万点比]|[零一二三四五六七八九十][ ]|(?<=[一二两三四五六七八九十])[年月日号分]|分之)";
const G5 = `(?:(?<=[一二两三四五六七八九十])(?:[a-zA-Z年月日号]|${commonUnits})|(?<=[一二两三四五六七八九十]\\s)[a-zA-Z])`;
const TAIL = "(?:[零幺一二两三四五六七八九十百千万亿点比]|分之)";
const mainPattern = new RegExp(`([a-z]\\s*)(${G3}+${G5}?)|(${G3}+(?:${G5}|${TAIL}+))`, "gi");

const pureNum = new RegExp(`^[零幺一二三四五六七八九]+(点[零幺一二三四五六七八九]+)* *([a-zA-Z]|${commonUnits})?$`);
const valueNum = new RegExp(`^十?(零?[一二两三四五六七八九十][十百千万]{1,2})*零?十?[一二三四五六七八九]?(点[零一二三四五六七八九]+)? *([a-zA-Z]|${commonUnits})?$`);
const consecutiveTens = new RegExp(`^((?:十[一二三四五六七八九])+)(${commonUnits})?$`);
const consecutiveHundreds = new RegExp(`^((?:[一二三四五六七八九]百零?[一二三四五六七八九])+)(${commonUnits})?$`);
const N = "[零一二三四五六七八九十百千万]+(?:点[零一二三四五六七八九]+)?";
const percentValue = new RegExp(`^(?<![一二三四五六七八九])百分之${N}$`);
const fractionValue = new RegExp(`^${N}分之${N}$`);
const ratioValue = new RegExp(`^${N}比${N}$`);
const timeValue = /^[零一二两三四五六七八九十]+点([零一二三四五六七八九十]+分)([零一二三四五六七八九十]+秒)?$/;
const dateValue = /^([零一二三四五六七八九十]+年)?([一二三四五六七八九十]+月)?([一二三四五六七八九十]+[日号])?$/;

// ---- helpers ----

function stripTrailingUnit(text) {
  const m = unitSuffix.exec(text);
  return m ? text.slice(0, m.index) : text;
}

function stripUnit(original) {
  const m = new RegExp(`(${commonUnits})$`).exec(original);
  let stripped, unit;
  if (m) {
    stripped = original.slice(0, m.index);
    unit = unitMapping[m[1]] === null ? m[1] : unitMapping[m[1]];
  } else {
    stripped = original;
    unit = "";
  }
  if (!unit && stripped) {
    const lm = /[a-zA-Z]+$/.exec(stripped);
    if (lm) { unit = lm[0]; stripped = stripped.slice(0, lm.index); }
  }
  return [stripped.trim(), unit];
}

function splitConsecutiveValue(text) {
  let unit = "";
  for (const c of commonUnits) {   // iterates characters, as the original does
    if (text.endsWith(c)) { unit = c; text = text.slice(0, -1); break; }
  }
  if (consecutiveTens.test(text + unit)) return text.match(/十[一二三四五六七八九]/g).map(convertValueNum).join(" ") + unit;
  if (consecutiveHundreds.test(text + unit)) return text.match(/[一二三四五六七八九]百零?[一二三四五六七八九]/g).map(convertValueNum).join(" ") + unit;
  return text + unit;
}

// ---- converters (throw on anything unexpected; the caller then keeps the original) ----

function convertPureNum(original, strict = false) {
  const [stripped, unit] = stripUnit(original);
  if (stripped === "一" && !strict) return original;
  let out = "";
  for (const c of stripped) {
    if (!(c in numMapper)) throw new Error("not a digit: " + c);
    out += numMapper[c];
  }
  return out + unit;
}

function convertValueNum(original) {
  let [stripped, unit] = stripUnit(original);
  if (!stripped.includes("点")) stripped += "点";
  const parts = stripped.split("点");
  if (parts.length !== 2) throw new Error("more than one 点");
  const [intPart, decimalPart] = parts;
  if (!intPart) return original;
  let value = 0, temp = 0, base = 1;
  for (const c of intPart) {
    if (c === "十") { temp = temp === 0 ? 10 : valueMapper[c] * temp; base = 1; }
    else if (c === "零") base = 1;
    else if ("一二两三四五六七八九".includes(c)) temp += valueMapper[c];
    else if (c === "万") { value += temp; value *= valueMapper[c]; base = valueMapper[c] / 10; temp = 0; }
    else if (c === "百" || c === "千") { value += temp * valueMapper[c]; base = valueMapper[c] / 10; temp = 0; }
  }
  value += temp * base;
  let final = String(value);
  const dec = convertPureNum(decimalPart, true);
  if (dec) final += "." + dec;
  return final + unit;
}

const convertFraction = (s) => { const [den, num] = s.split("分之"); return convertValueNum(num) + "/" + convertValueNum(den); };
const convertPercent = (s) => convertValueNum(s.slice(3)) + "%";
const convertRatio = (s) => { const [a, b] = s.split("比"); return convertValueNum(a) + ":" + convertValueNum(b); };

function convertTime(original) {
  const res = original.split(/[点分秒]/).filter(Boolean);
  let final = convertValueNum(res[0]).padStart(2, "0");
  final += ":" + convertValueNum(res[1]).padStart(2, "0");
  if (res.length > 2) final += ":" + convertValueNum(res[2]).padStart(2, "0");
  if (res.length > 3) final += "." + convertPureNum(res[3]);
  return final;
}

function convertDate(original) {
  let final = "";
  const cut = (sep) => { const i = original.indexOf(sep); const head = original.slice(0, i); original = original.slice(i + 1); return head; };
  if (original.includes("年")) final += convertPureNum(cut("年")) + "年";
  if (original.includes("月")) final += convertValueNum(cut("月")) + "月";
  if (original.includes("日")) final += convertValueNum(cut("日")) + "日";
  else if (original.includes("号")) final += convertValueNum(cut("号")) + "号";
  return final;
}

// ---- main ----

function replace(string, head, text, start) {
  const end = start + text.length;
  const lPos = Math.max(start - 2, 0);
  let final;
  try {
    if (idioms.some((idiom) => { const i = string.indexOf(idiom); return i >= lPos && i < end; })) final = text;
    else if (text === "十分" && string[end] !== "钟") final = text;   // 十分重要: "very", not 10 minutes
    else if (fuzzyRegex.test(text)) final = text;
    else if (rangeDetect.test(text)) final = convertRangeExpression(text);
    else if (timeValue.test(text)) final = convertTime(text);
    else if (pureNum.test(stripTrailingUnit(text))) final = convertPureNum(text);
    else if (consecutiveTens.test(text) || consecutiveHundreds.test(text)) final = splitConsecutiveValue(text);
    else if (valueNum.test(stripTrailingUnit(text))) final = convertValueNum(text);
    else if (percentValue.test(text)) final = convertPercent(text);
    else if (fractionValue.test(text)) final = convertFraction(text);
    else if (ratioValue.test(text)) final = convertRatio(text);
    else if (dateValue.test(text)) final = convertDate(text);
    else final = text;
  } catch {
    final = text;
  }
  return (head || "") + final;
}

function chineseToNum(text) {
  return text.replace(mainPattern, (_whole, head, withHead, noHead, offset) => {
    const span = head !== undefined ? withHead : noHead;
    const start = head !== undefined ? offset + head.length : offset;
    return replace(text, head, span, start);
  });
}

module.exports = { chineseToNum };
