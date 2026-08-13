// Reference signer mirroring connectonion-ts/src/address.ts (Node path) exactly.
// Given a 32-byte seed (hex), prints the address, the canonical JSON of a fixed
// payload, and the Ed25519 signature — the ground truth the C# port must match.
//
// Usage: node ref-sign.js <seedHex32>

const crypto = require('crypto');

const seedHex = process.argv[2];
if (!seedHex || Buffer.from(seedHex, 'hex').length !== 32) {
  console.error('Provide a 32-byte seed as hex.');
  process.exit(2);
}
const seed = Buffer.from(seedHex, 'hex');

// Recreate the private key from the raw seed using the PKCS#8 Ed25519 prefix,
// exactly as address.ts load()/sign() do.
const privateKey = crypto.createPrivateKey({
  key: Buffer.concat([Buffer.from('302e020100300506032b657004220420', 'hex'), seed]),
  format: 'der',
  type: 'pkcs8',
});
const publicKey = crypto.createPublicKey(privateKey);
const pub = publicKey.export({ type: 'spki', format: 'der' }).slice(-32);
const address = '0x' + pub.toString('hex');

// canonicalJSON: sorted keys, JSON.stringify (address.ts).
function canonicalJSON(obj) {
  const sortedKeys = Object.keys(obj).sort();
  const sortedObj = {};
  for (const key of sortedKeys) sortedObj[key] = obj[key];
  return JSON.stringify(sortedObj);
}

// A CONNECT-shaped payload plus a few tricky value types to stress escaping.
const payload = {
  to: address,
  timestamp: 1700000000,
  invite_code: 'abc+DEF/123',
  payment: 5,
};

const canonical = canonicalJSON(payload);
const signature = crypto.sign(null, Buffer.from(canonical), privateKey).toString('hex');

console.log(JSON.stringify({ address, canonical, signature }));
