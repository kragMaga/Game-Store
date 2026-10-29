import { useState } from 'react';
import { ArrowRight, Boxes, ShieldCheck } from 'lucide-react';
import { post, type LoginResult } from '../api';
import { useSession } from '../session';
import { Button, ErrorNotice } from '../components/ui';

// MFA challenges stay in component memory and are discarded when the user goes
// back. Registration never assumes confirmation email delivery has completed.
export default function Auth() {
  const [mode, setMode] = useState<'login' | 'register' | 'forgot' | 'resend'>('login');
  const [challenge, setChallenge] = useState<string>();
  const [name, setName] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>();
  const [message, setMessage] = useState('');
  const session = useSession();
  function change(next: typeof mode) {
    setMode(next);
    setChallenge(undefined);
    setMessage('');
    setError(undefined);
  }
  return (
    <main className="auth-layout">
      <section className="auth-story">
        <a className="brand" href="/">
          <span className="brand-icon">
            <Boxes />
          </span>
          TradeNest
        </a>
        <div>
          <span className="eyebrow light">A clearer view of your business</span>
          <h1>
            Know what’s in stock.
            <br />
            Know what’s next.
          </h1>
          <p>Your products, stock levels, and business activity in one organized workspace.</p>
          <div className="story-grid">
            <span>01 / Track inventory</span>
            <span>02 / Record activity</span>
            <span>03 / Plan ahead</span>
          </div>
        </div>
        <span className="story-footer">
          <ShieldCheck size={16} /> A separate workspace for every business
        </span>
      </section>
      <section className="auth-form">
        <div className="auth-card">
          <span className="eyebrow">Your business, organized</span>
          <h2>
            {challenge
              ? 'One more step'
              : mode === 'login'
                ? 'Welcome back'
                : mode === 'register'
                  ? 'Create your account'
                  : mode === 'forgot'
                    ? 'Reset your password'
                    : 'Confirm your email'}
          </h2>
          <p className="muted">
            {challenge
              ? 'Enter an authenticator code or a recovery code.'
              : mode === 'login'
                ? 'Sign in to your inventory workspace.'
                : mode === 'register'
                  ? 'Create an account, confirm your email, then set up your business.'
                  : 'Enter your email and we’ll send the next steps if your account is eligible.'}
          </p>
          <form
            key={`${mode}-${!!challenge}`}
            onSubmit={async (event) => {
              event.preventDefault();
              const form = event.currentTarget;
              const data = new FormData(form);
              setBusy(true);
              setError(undefined);
              setMessage('');
              try {
                if (mode === 'login') {
                  const username = challenge ? name : String(data.get('username'));
                  const result = challenge
                    ? await post<LoginResult>('/auth/login/mfa', {
                        challengeToken: challenge,
                        code: data.get('code'),
                      })
                    : await post<LoginResult>('/auth/login', {
                        username,
                        password: data.get('password'),
                      });
                  if ('requiresTwoFactor' in result) {
                    setName(username);
                    setChallenge(result.challengeToken);
                  } else session.signIn(username, result);
                } else if (mode === 'register') {
                  const result = await post<{ message: string }>('/auth/register', {
                    username: data.get('username'),
                    email: data.get('email'),
                    password: data.get('password'),
                  });
                  setMessage(
                    result.message.includes('could not')
                      ? 'Your account was created, but the confirmation email could not be sent. Use Resend confirmation below to try again.'
                      : 'Account created. Check your email and confirm your address before signing in.',
                  );
                } else {
                  await post(
                    mode === 'forgot' ? '/auth/password/forgot' : '/auth/email/resend-confirmation',
                    { email: data.get('email') },
                  );
                  setMessage(
                    'If your account is eligible, an email with the next steps will arrive shortly.',
                  );
                }
              } catch (error) {
                setError(error);
              } finally {
                const password = form.elements.namedItem('password') as HTMLInputElement | null;
                if (password) password.value = '';
                setBusy(false);
              }
            }}
          >
            {challenge ? (
              <label>
                Verification code
                <input name="code" autoComplete="one-time-code" autoFocus required />
              </label>
            ) : (
              <>
                {(mode === 'login' || mode === 'register') && (
                  <label>
                    Username
                    <input
                      name="username"
                      autoComplete="username"
                      required
                      minLength={mode === 'register' ? 5 : undefined}
                      maxLength={20}
                      pattern={mode === 'register' ? '[A-Za-z0-9._-]{5,20}' : undefined}
                    />
                  </label>
                )}
                {mode !== 'login' && (
                  <label>
                    Email address
                    <input
                      name="email"
                      type="email"
                      autoComplete="email"
                      required
                      maxLength={254}
                    />
                  </label>
                )}
                {(mode === 'login' || mode === 'register') && (
                  <label>
                    Password
                    <input
                      name="password"
                      type="password"
                      autoComplete={mode === 'register' ? 'new-password' : 'current-password'}
                      required
                      minLength={mode === 'register' ? 8 : undefined}
                    />
                    {mode === 'register' && (
                      <small className="muted">
                        At least 8 characters, including uppercase, lowercase, and a number.
                      </small>
                    )}
                  </label>
                )}
              </>
            )}
            <ErrorNotice error={error} />
            {message && (
              <p role="status" className="notice success">
                {message}
              </p>
            )}
            <Button busy={busy} className="w-full">
              {challenge
                ? 'Verify and sign in'
                : mode === 'login'
                  ? 'Sign in'
                  : mode === 'register'
                    ? 'Create account'
                    : 'Send instructions'}
              <ArrowRight size={17} />
            </Button>
          </form>
          <div className="auth-links">
            {(mode !== 'login' || challenge) && (
              <button onClick={() => change('login')}>Back to sign in</button>
            )}
            {mode === 'login' && !challenge && (
              <>
                <button onClick={() => change('register')}>Create an account</button>
                <button onClick={() => change('forgot')}>Forgot password?</button>
              </>
            )}
            <button onClick={() => change('resend')}>Resend confirmation</button>
          </div>
        </div>
      </section>
    </main>
  );
}
