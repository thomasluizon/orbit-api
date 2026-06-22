import { fail } from 'k6';

const PROD_HOSTS = ['api.useorbit.org'];

function readBaseUrl() {
  const raw = (__ENV.BASE_URL || '').trim();
  if (!raw) {
    return 'http://localhost:8080';
  }
  return raw.replace(/\/+$/, '');
}

function isProd(baseUrl) {
  try {
    const host = new URL(baseUrl).hostname.toLowerCase();
    return PROD_HOSTS.includes(host);
  } catch (_error) {
    return false;
  }
}

export const BASE_URL = readBaseUrl();
const targetIsProd = isProd(BASE_URL);

export function requireProdOptIn() {
  if (targetIsProd && (__ENV.ALLOW_PROD || '').trim() !== 'i-understand') {
    fail(
      `Refusing to run against ${BASE_URL}. This is production. ` +
        'Re-run with ALLOW_PROD=i-understand only inside an agreed off-peak window, ' +
        'per load-tests/README.md.'
    );
  }
}

export function readNumber(name, fallback) {
  const raw = (__ENV[name] || '').trim();
  if (!raw) {
    return fallback;
  }
  const parsed = Number(raw);
  return Number.isFinite(parsed) ? parsed : fallback;
}

export function readString(name, fallback) {
  const raw = (__ENV[name] || '').trim();
  return raw || fallback;
}

export const COMMON_HEADERS = {
  Accept: 'application/json',
};
