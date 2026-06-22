import http from 'k6/http';
import { fail } from 'k6';
import { BASE_URL, COMMON_HEADERS, readString } from './config.js';

function loginWithTestAccount() {
  const email = readString('TEST_EMAIL', '');
  const code = readString('TEST_CODE', '');
  if (!email || !code) {
    fail(
      'No ACCESS_TOKEN provided and TEST_EMAIL/TEST_CODE missing. ' +
        'Against prod, log in manually once and pass ACCESS_TOKEN (+ optional REFRESH_TOKEN). ' +
        'Against a non-prod target with a TEST_ACCOUNTS entry, pass TEST_EMAIL and TEST_CODE. ' +
        'See load-tests/README.md.'
    );
  }

  const response = http.post(
    `${BASE_URL}/api/auth/verify-code`,
    JSON.stringify({ email, code, language: 'en' }),
    { headers: { ...COMMON_HEADERS, 'Content-Type': 'application/json' }, tags: { name: 'auth_login_setup' } }
  );

  if (response.status !== 200) {
    fail(`Login failed (${response.status}). Body: ${response.body}`);
  }

  const body = response.json();
  if (!body || !body.token) {
    fail(`Login response missing token. Body: ${response.body}`);
  }

  return { accessToken: body.token, refreshToken: body.refreshToken || '' };
}

export function acquireSession() {
  const presetAccess = readString('ACCESS_TOKEN', '');
  if (presetAccess) {
    return { accessToken: presetAccess, refreshToken: readString('REFRESH_TOKEN', '') };
  }
  return loginWithTestAccount();
}

export function authHeaders(session) {
  return { ...COMMON_HEADERS, Authorization: `Bearer ${session.accessToken}` };
}
