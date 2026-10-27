const OBF_KEY = 'example-key-v9';
function xor(str) { return str; }
export function deobfuscate(blob) { return JSON.parse(xor(atob(blob))); }
