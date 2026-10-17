namespace Orbit.Api.OAuth;

/// <summary>
/// The authorize page's behaviour. It reads its parameters and its words from the two JSON blocks the page
/// renders, so no request value and no localized string is ever generated into executable source.
/// </summary>
public static class OAuthPageScript
{
    /// <summary>The whole script, served inline with the page.</summary>
    public const string Js = """
const request = JSON.parse(document.getElementById('oauth-request').textContent);
const copy = JSON.parse(document.getElementById('oauth-copy').textContent);
const CODE_LENGTH = 6;
const RESEND_SECONDS = 60;

const stepTitle = document.getElementById('step-title');
const stepBody = document.getElementById('step-body');
const status = document.getElementById('status');
const errorLine = document.getElementById('error');
const emailStep = document.getElementById('step-email');
const emailField = document.getElementById('email-field');
const emailInput = document.getElementById('email');
const emailCaption = document.getElementById('email-error');
const sendButton = document.getElementById('send-code');
const codeStep = document.getElementById('step-code');
const codeGroup = document.getElementById('code');
const codeInput = document.getElementById('code-input');
const codeCaption = document.getElementById('code-caption');
const cells = Array.from(document.querySelectorAll('[data-cell]'));
const verifyButton = document.getElementById('verify');
const resendButton = document.getElementById('resend');
const countdown = document.getElementById('resend-countdown');

let email = '';
let resendTicker = null;
let pending = false;

function show(element, visible) {
  element.classList.toggle('hidden', !visible);
}

function setAlert(message) {
  errorLine.textContent = message || '';
  show(errorLine, Boolean(message));
}

function setStatus(message) {
  status.textContent = message || '';
  show(status, Boolean(message));
}

function setEmailError(message) {
  emailCaption.textContent = message || '';
  show(emailCaption, Boolean(message));
  emailField.toggleAttribute('data-error', Boolean(message));
  emailInput.setAttribute('aria-invalid', message ? 'true' : 'false');
}

function setCodeError(message) {
  codeGroup.toggleAttribute('data-error', Boolean(message));
  codeCaption.textContent = message || copy.codeHint;
  codeCaption.classList.toggle('caption', Boolean(message));
  codeCaption.classList.toggle('note', !message);
  codeInput.setAttribute('aria-invalid', message ? 'true' : 'false');
}

function refreshActions() {
  sendButton.disabled = pending || emailInput.value.trim().length === 0;
  resendButton.disabled = pending;
  verifyButton.disabled = pending || codeInput.value.length !== CODE_LENGTH;
}

function busy(button, isBusy) {
  pending = isBusy;
  if (isBusy) button.setAttribute('aria-busy', 'true');
  else button.removeAttribute('aria-busy');
  refreshActions();
}

function drawCells() {
  const digits = codeInput.value;
  const active = Math.min(digits.length, CODE_LENGTH - 1);
  const focused = document.activeElement === codeInput && !codeInput.disabled;
  cells.forEach((cell, index) => {
    cell.textContent = digits[index] || '';
    cell.toggleAttribute('data-active', focused && index === active);
  });
}

function formatCountdown(seconds) {
  const minutes = Math.floor(seconds / 60);
  const rest = seconds % 60;
  return minutes + ':' + String(rest).padStart(2, '0');
}

function startResendWindow() {
  let remaining = RESEND_SECONDS;
  clearInterval(resendTicker);
  show(resendButton, false);
  show(countdown, true);
  countdown.textContent = copy.resendCountdown.replace('{0}', formatCountdown(remaining));
  resendTicker = setInterval(() => {
    remaining -= 1;
    if (remaining <= 0) {
      clearInterval(resendTicker);
      show(countdown, false);
      show(resendButton, true);
      return;
    }
    countdown.textContent = copy.resendCountdown.replace('{0}', formatCountdown(remaining));
  }, 1000);
}

function showEmailStep() {
  clearInterval(resendTicker);
  stepTitle.textContent = copy.emailTitle;
  stepBody.textContent = copy.emailBody;
  setStatus('');
  setAlert('');
  setCodeError('');
  show(emailStep, true);
  show(codeStep, false);
  refreshActions();
  emailInput.focus();
}

function showCodeStep(resent) {
  stepTitle.textContent = copy.codeTitle;
  stepBody.textContent = copy.sentToLine.replace('{0}', email);
  setStatus(resent ? copy.codeResent : copy.codeSent);
  setAlert('');
  setCodeError('');
  codeInput.value = '';
  codeInput.disabled = false;
  codeGroup.removeAttribute('data-disabled');
  drawCells();
  refreshActions();
  show(emailStep, false);
  show(codeStep, true);
  startResendWindow();
  codeInput.focus();
}

async function post(path, payload) {
  const response = await fetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload)
  });
  const data = await response.json().catch(() => ({}));
  return { ok: response.ok, data };
}

async function sendCode(resent) {
  const typed = emailInput.value.trim();
  if (!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(typed)) {
    setEmailError(copy.emailInvalid);
    emailInput.focus();
    return;
  }
  setEmailError('');
  setAlert('');
  const button = resent ? resendButton : sendButton;
  busy(button, true);
  try {
    const result = await post('/oauth/send-code', { email: typed, language: request.language });
    if (!result.ok) {
      setAlert(result.data.error || copy.sendFailed);
      return;
    }
    email = typed;
    showCodeStep(resent);
  } catch {
    setAlert(copy.sendFailed);
  } finally {
    busy(button, false);
  }
}

async function verifyCode() {
  const code = codeInput.value;
  setCodeError('');
  setAlert('');
  busy(verifyButton, true);
  codeInput.disabled = true;
  codeGroup.setAttribute('data-disabled', '');
  drawCells();
  try {
    const result = await post('/oauth/verify-code', {
      email: email,
      code: code,
      state: request.state,
      codeChallenge: request.codeChallenge,
      redirectUri: request.redirectUri,
      clientId: request.clientId,
      nonce: request.nonce
    });
    if (!result.ok) {
      setCodeError(result.data.error || copy.verifyFailed);
      codeInput.disabled = false;
      codeGroup.removeAttribute('data-disabled');
      codeInput.focus();
      drawCells();
      return;
    }
    setStatus(copy.authorizedLine);
    window.location.href = result.data.redirectUrl;
  } catch {
    setAlert(copy.sendFailed);
    codeInput.disabled = false;
    codeGroup.removeAttribute('data-disabled');
    drawCells();
  } finally {
    busy(verifyButton, false);
  }
}

codeInput.addEventListener('input', () => {
  const digits = codeInput.value.replace(/\D/g, '').slice(0, CODE_LENGTH);
  codeInput.value = digits;
  setCodeError('');
  drawCells();
  refreshActions();
  if (digits.length === CODE_LENGTH) verifyCode();
});
codeInput.addEventListener('focus', drawCells);
codeInput.addEventListener('blur', drawCells);
codeGroup.addEventListener('click', () => codeInput.focus());

emailInput.addEventListener('input', () => {
  setEmailError('');
  refreshActions();
});
emailInput.addEventListener('keydown', (event) => {
  if (event.key === 'Enter') sendCode(false);
});

sendButton.addEventListener('click', () => sendCode(false));
resendButton.addEventListener('click', () => sendCode(true));
verifyButton.addEventListener('click', verifyCode);
document.getElementById('change-email').addEventListener('click', showEmailStep);

drawCells();
refreshActions();
if (request.error) setAlert(request.error);
""";
}
