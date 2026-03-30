namespace Orbit.Api.OAuth;

public static class OAuthLoginPage
{
    public static string Render(string clientId, string redirectUri, string state,
        string codeChallenge, string codeChallengeMethod, string googleClientId)
    {
        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Authorize Orbit</title>
    <link rel="preconnect" href="https://fonts.googleapis.com">
    <link href="https://fonts.googleapis.com/css2?family=Manrope:wght@400;500;600;700&display=swap" rel="stylesheet">
    <script src="https://accounts.google.com/gsi/client" async defer></script>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body {
            font-family: 'Manrope', sans-serif;
            background: #0a0a0f;
            color: #e4e4e7;
            min-height: 100vh;
            display: flex;
            align-items: center;
            justify-content: center;
            padding: 1rem;
        }
        .card {
            background: #16161e;
            border: 1px solid #27272a;
            border-radius: 1.25rem;
            padding: 2.5rem 2rem;
            width: 100%;
            max-width: 400px;
        }
        .logo {
            width: 48px;
            height: 48px;
            background: linear-gradient(135deg, #a78bfa, #7c3aed);
            border-radius: 12px;
            margin: 0 auto 1.25rem;
            display: flex;
            align-items: center;
            justify-content: center;
            font-size: 1.5rem;
            font-weight: 700;
            color: white;
        }
        h1 {
            text-align: center;
            font-size: 1.25rem;
            font-weight: 700;
            margin-bottom: 0.25rem;
        }
        .subtitle {
            text-align: center;
            color: #a1a1aa;
            font-size: 0.875rem;
            margin-bottom: 2rem;
        }
        .form-group { margin-bottom: 1rem; }
        label {
            display: block;
            font-size: 0.75rem;
            font-weight: 600;
            text-transform: uppercase;
            letter-spacing: 0.05em;
            color: #a1a1aa;
            margin-bottom: 0.5rem;
        }
        input {
            width: 100%;
            padding: 0.75rem 1rem;
            background: #0a0a0f;
            border: 1px solid #27272a;
            border-radius: 0.75rem;
            color: #e4e4e7;
            font-family: 'Manrope', sans-serif;
            font-size: 0.9375rem;
            outline: none;
            transition: border-color 0.2s;
        }
        input:focus { border-color: #a78bfa; }
        input::placeholder { color: #52525b; }
        .btn {
            width: 100%;
            padding: 0.75rem;
            border: none;
            border-radius: 0.75rem;
            font-family: 'Manrope', sans-serif;
            font-size: 0.9375rem;
            font-weight: 600;
            cursor: pointer;
            transition: opacity 0.2s, transform 0.1s;
        }
        .btn:active { transform: scale(0.98); }
        .btn:disabled { opacity: 0.5; cursor: not-allowed; }
        .btn-primary {
            background: linear-gradient(135deg, #a78bfa, #7c3aed);
            color: white;
        }
        .btn-primary:hover:not(:disabled) { opacity: 0.9; }
        .divider {
            display: flex;
            align-items: center;
            gap: 1rem;
            margin: 1.5rem 0;
            color: #52525b;
            font-size: 0.8125rem;
        }
        .divider::before, .divider::after {
            content: '';
            flex: 1;
            height: 1px;
            background: #27272a;
        }
        .google-btn {
            width: 100%;
            padding: 0.75rem;
            background: #1f1f28;
            border: 1px solid #27272a;
            border-radius: 0.75rem;
            color: #e4e4e7;
            font-family: 'Manrope', sans-serif;
            font-size: 0.9375rem;
            font-weight: 600;
            cursor: pointer;
            display: flex;
            align-items: center;
            justify-content: center;
            gap: 0.625rem;
            transition: background 0.2s;
        }
        .google-btn:hover { background: #27272f; }
        .google-btn svg { width: 20px; height: 20px; }
        .error {
            background: rgba(239, 68, 68, 0.1);
            border: 1px solid rgba(239, 68, 68, 0.3);
            color: #fca5a5;
            padding: 0.625rem 0.875rem;
            border-radius: 0.5rem;
            font-size: 0.8125rem;
            margin-bottom: 1rem;
            display: none;
        }
        .success {
            background: rgba(34, 197, 94, 0.1);
            border: 1px solid rgba(34, 197, 94, 0.3);
            color: #86efac;
            padding: 0.625rem 0.875rem;
            border-radius: 0.5rem;
            font-size: 0.8125rem;
            margin-bottom: 1rem;
            display: none;
        }
        .back-link {
            display: inline-flex;
            align-items: center;
            gap: 0.25rem;
            color: #a78bfa;
            font-size: 0.8125rem;
            cursor: pointer;
            margin-bottom: 1rem;
            border: none;
            background: none;
            font-family: 'Manrope', sans-serif;
        }
        .back-link:hover { text-decoration: underline; }
        .hidden { display: none !important; }
        .loading { position: relative; pointer-events: none; }
        .loading::after {
            content: '';
            position: absolute;
            inset: 0;
            background: rgba(0,0,0,0.3);
            border-radius: 0.75rem;
        }
        .code-inputs {
            display: flex;
            gap: 0.5rem;
            justify-content: center;
            margin-bottom: 1rem;
        }
        .code-inputs input {
            width: 3rem;
            height: 3.5rem;
            text-align: center;
            font-size: 1.5rem;
            font-weight: 700;
            padding: 0;
            letter-spacing: 0;
        }
        .resend {
            text-align: center;
            margin-bottom: 1.25rem;
        }
        .resend button {
            background: none;
            border: none;
            color: #a78bfa;
            font-family: 'Manrope', sans-serif;
            font-size: 0.8125rem;
            cursor: pointer;
        }
        .resend button:disabled { color: #52525b; cursor: not-allowed; }
        .resend button:hover:not(:disabled) { text-decoration: underline; }
        .permissions {
            margin-top: 1.5rem;
            padding-top: 1rem;
            border-top: 1px solid #27272a;
            font-size: 0.8125rem;
            color: #71717a;
        }
        .permissions strong { color: #a1a1aa; }
    </style>
</head>
<body>
    <div class="card">
        <div class="logo">O</div>
        <h1>Authorize Orbit</h1>
        <p class="subtitle">Connect your Orbit account to Claude</p>

        <div id="error" class="error"></div>
        <div id="success" class="success"></div>

        <!-- Step 1: Email -->
        <div id="step-email">
            <div class="form-group">
                <label for="email">Email</label>
                <input type="email" id="email" placeholder="you@example.com" autocomplete="email" autofocus>
            </div>
            <button class="btn btn-primary" id="send-code-btn" onclick="sendCode()">Send code</button>

            <div class="divider">or</div>

            <div id="google-btn-container">
                <button class="google-btn" id="google-signin-btn" onclick="googleSignIn()">
                    <svg viewBox="0 0 24 24"><path fill="#4285F4" d="M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92a5.06 5.06 0 0 1-2.2 3.32v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.1z"/><path fill="#34A853" d="M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z"/><path fill="#FBBC05" d="M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.07H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.93l2.85-2.22.81-.62z"/><path fill="#EA4335" d="M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.07l3.66 2.84c.87-2.6 3.3-4.53 6.16-4.53z"/></svg>
                    Continue with Google
                </button>
            </div>
        </div>

        <!-- Step 2: Code -->
        <div id="step-code" class="hidden">
            <button class="back-link" onclick="showEmailStep()">&#8592; Back</button>
            <p style="text-align:center;color:#a1a1aa;font-size:0.875rem;margin-bottom:1.25rem;">
                Enter the 6-digit code sent to <strong id="email-display" style="color:#e4e4e7;"></strong>
            </p>
            <div class="code-inputs" id="code-inputs">
                <input type="text" maxlength="1" inputmode="numeric" pattern="[0-9]" autocomplete="one-time-code">
                <input type="text" maxlength="1" inputmode="numeric" pattern="[0-9]">
                <input type="text" maxlength="1" inputmode="numeric" pattern="[0-9]">
                <input type="text" maxlength="1" inputmode="numeric" pattern="[0-9]">
                <input type="text" maxlength="1" inputmode="numeric" pattern="[0-9]">
                <input type="text" maxlength="1" inputmode="numeric" pattern="[0-9]">
            </div>
            <div class="resend">
                <button id="resend-btn" onclick="sendCode()" disabled>Resend code</button>
            </div>
            <button class="btn btn-primary" id="verify-btn" onclick="verifyCode()">Verify & Authorize</button>
        </div>

        <div class="permissions">
            <strong>Orbit will allow Claude to:</strong>
            <ul style="margin-top:0.5rem;padding-left:1.25rem;">
                <li>Manage your habits, goals, and tags</li>
                <li>View your profile and stats</li>
                <li>Log completions and track progress</li>
            </ul>
        </div>
    </div>

    <script>
        const oauthParams = {
            clientId: '{{clientId}}',
            redirectUri: '{{redirectUri}}',
            state: '{{state}}',
            codeChallenge: '{{codeChallenge}}',
            codeChallengeMethod: '{{codeChallengeMethod}}'
        };
        const googleClientId = '{{googleClientId}}';
        let userEmail = '';
        let resendTimer = null;

        // Code input behavior
        const codeInputs = document.querySelectorAll('#code-inputs input');
        codeInputs.forEach((input, i) => {
            input.addEventListener('input', (e) => {
                const val = e.target.value.replace(/\D/g, '');
                e.target.value = val;
                if (val && i < 5) codeInputs[i + 1].focus();
                if (getCode().length === 6) verifyCode();
            });
            input.addEventListener('keydown', (e) => {
                if (e.key === 'Backspace' && !e.target.value && i > 0) {
                    codeInputs[i - 1].focus();
                }
            });
            input.addEventListener('paste', (e) => {
                e.preventDefault();
                const text = (e.clipboardData || window.clipboardData).getData('text').replace(/\D/g, '');
                for (let j = 0; j < 6 && j < text.length; j++) {
                    codeInputs[j].value = text[j];
                }
                if (text.length >= 6) verifyCode();
                else if (text.length > 0) codeInputs[Math.min(text.length, 5)].focus();
            });
        });

        function getCode() {
            return Array.from(codeInputs).map(i => i.value).join('');
        }

        function showError(msg) {
            const el = document.getElementById('error');
            el.textContent = msg;
            el.style.display = 'block';
            document.getElementById('success').style.display = 'none';
        }

        function showSuccess(msg) {
            const el = document.getElementById('success');
            el.textContent = msg;
            el.style.display = 'block';
            document.getElementById('error').style.display = 'none';
        }

        function clearMessages() {
            document.getElementById('error').style.display = 'none';
            document.getElementById('success').style.display = 'none';
        }

        function showEmailStep() {
            document.getElementById('step-email').classList.remove('hidden');
            document.getElementById('step-code').classList.add('hidden');
            clearMessages();
        }

        function startResendTimer() {
            const btn = document.getElementById('resend-btn');
            let seconds = 60;
            btn.disabled = true;
            btn.textContent = 'Resend code (' + seconds + 's)';
            clearInterval(resendTimer);
            resendTimer = setInterval(() => {
                seconds--;
                if (seconds <= 0) {
                    clearInterval(resendTimer);
                    btn.disabled = false;
                    btn.textContent = 'Resend code';
                } else {
                    btn.textContent = 'Resend code (' + seconds + 's)';
                }
            }, 1000);
        }

        async function sendCode() {
            const email = document.getElementById('email').value.trim();
            if (!email) { showError('Please enter your email'); return; }

            const btn = document.getElementById('send-code-btn');
            btn.disabled = true;
            clearMessages();

            try {
                const res = await fetch('/oauth/send-code', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ email })
                });
                const data = await res.json();
                if (!res.ok) { showError(data.error || 'Failed to send code'); btn.disabled = false; return; }

                userEmail = email;
                document.getElementById('email-display').textContent = email;
                document.getElementById('step-email').classList.add('hidden');
                document.getElementById('step-code').classList.remove('hidden');
                codeInputs.forEach(i => i.value = '');
                codeInputs[0].focus();
                startResendTimer();
                showSuccess('Code sent to ' + email);
            } catch {
                showError('Network error. Please try again.');
            }
            btn.disabled = false;
        }

        async function verifyCode() {
            const code = getCode();
            if (code.length !== 6) { showError('Please enter the full 6-digit code'); return; }

            const btn = document.getElementById('verify-btn');
            btn.disabled = true;
            btn.textContent = 'Verifying...';
            clearMessages();

            try {
                const res = await fetch('/oauth/verify-code', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        email: userEmail,
                        code,
                        state: oauthParams.state,
                        codeChallenge: oauthParams.codeChallenge,
                        redirectUri: oauthParams.redirectUri,
                        clientId: oauthParams.clientId
                    })
                });
                const data = await res.json();
                if (!res.ok) {
                    showError(data.error || 'Verification failed');
                    btn.disabled = false;
                    btn.textContent = 'Verify & Authorize';
                    return;
                }
                showSuccess('Authorized! Redirecting...');
                window.location.href = data.redirectUrl;
            } catch {
                showError('Network error. Please try again.');
                btn.disabled = false;
                btn.textContent = 'Verify & Authorize';
            }
        }

        // Google Sign-In
        function googleSignIn() {
            if (!googleClientId) { showError('Google sign-in is not configured'); return; }
            google.accounts.id.initialize({
                client_id: googleClientId,
                callback: handleGoogleCredential,
                ux_mode: 'popup'
            });
            google.accounts.id.prompt();
        }

        async function handleGoogleCredential(response) {
            clearMessages();
            const btn = document.getElementById('google-signin-btn');
            btn.disabled = true;

            try {
                const res = await fetch('/oauth/google', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        credential: response.credential,
                        state: oauthParams.state,
                        codeChallenge: oauthParams.codeChallenge,
                        redirectUri: oauthParams.redirectUri,
                        clientId: oauthParams.clientId
                    })
                });
                const data = await res.json();
                if (!res.ok) {
                    showError(data.error || 'Google sign-in failed');
                    btn.disabled = false;
                    return;
                }
                showSuccess('Authorized! Redirecting...');
                window.location.href = data.redirectUrl;
            } catch {
                showError('Network error. Please try again.');
                btn.disabled = false;
            }
        }

        // Enter key on email
        document.getElementById('email').addEventListener('keydown', (e) => {
            if (e.key === 'Enter') sendCode();
        });
    </script>
</body>
</html>
""";
    }
}
