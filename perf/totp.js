// Prompt 77 — TOTP (RFC 6238) para o harness de carga k6.
//
// Gera o código de 6 dígitos que um AUTENTICADOR real produziria, a partir do segredo Base32 de um
// USUÁRIO DE TESTE semeado no ambiente DEDICADO (passado por variável de ambiente — nunca hardcoded,
// nunca credencial de produção). NÃO é bypass de MFA: o servidor continua validando o segundo fator
// pelo fluxo real (POST /conta/mfa). Espelha src/.../ProvedorTotpRfc6238 e o CodigoTotpTeste do E2E
// (HMAC-SHA1, 6 dígitos, passo de 30 s).

import crypto from 'k6/crypto';

const ALFABETO_BASE32 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
const DIGITOS = 6;
const PASSO_SEGUNDOS = 30;

function base32Decode(base32) {
  const normalizado = base32.replace(/=+$/, '').toUpperCase();
  const bytes = [];
  let buffer = 0;
  let bits = 0;
  for (let i = 0; i < normalizado.length; i++) {
    const indice = ALFABETO_BASE32.indexOf(normalizado[i]);
    if (indice < 0) {
      throw new Error('Segredo Base32 invalido.');
    }
    buffer = (buffer << 5) | indice;
    bits += 5;
    if (bits >= 8) {
      bits -= 8;
      bytes.push((buffer >> bits) & 0xff);
    }
  }
  return new Uint8Array(bytes);
}

// Contador de 8 bytes big-endian (aritmetica segura ate 2^53 — cobre os passos de tempo com folga).
function contadorBytes(contador) {
  const b = new Uint8Array(8);
  let n = contador;
  for (let i = 7; i >= 0; i--) {
    b[i] = n & 0xff;
    n = Math.floor(n / 256);
  }
  return b;
}

function hexParaBytes(hex) {
  const out = new Uint8Array(hex.length / 2);
  for (let i = 0; i < out.length; i++) {
    out[i] = parseInt(hex.substr(i * 2, 2), 16);
  }
  return out;
}

// Codigo TOTP valido no instante informado (segundos epoch). Pede saida 'hex' ao k6/crypto para evitar a
// ambiguidade ArrayBuffer-vs-string do modo 'binary' entre versoes do k6.
export function gerarTotp(segredoBase32, epochSegundos) {
  if (!segredoBase32) {
    throw new Error('Segredo TOTP ausente.');
  }
  const passo = Math.floor(epochSegundos / PASSO_SEGUNDOS);
  const chave = base32Decode(segredoBase32);
  const mensagem = contadorBytes(passo);

  const hex = crypto.hmac('sha1', chave.buffer, mensagem.buffer, 'hex');
  const hash = hexParaBytes(hex);

  const offset = hash[hash.length - 1] & 0x0f;
  const binario =
    ((hash[offset] & 0x7f) << 24) |
    ((hash[offset + 1] & 0xff) << 16) |
    ((hash[offset + 2] & 0xff) << 8) |
    (hash[offset + 3] & 0xff);

  const otp = binario % Math.pow(10, DIGITOS);
  return otp.toString().padStart(DIGITOS, '0');
}
