import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { acceptTokens, logout, onSessionExpired, post, setProof, type LoginResult } from './api';
import { Button, ErrorNotice, Modal } from './components/ui';

type Session = {
  username: string | null;
  signIn: (username: string, result: LoginResult) => void;
  signOut: () => Promise<void>;
  reauthenticate: () => void;
};
const Context = createContext<Session | null>(null);
export function SessionProvider({ children }: { children: ReactNode }) {
  const [username, setUsername] = useState<string | null>(null);
  const [showProof, setShowProof] = useState(false);
  const query = useQueryClient();
  // Clearing caches on every account change prevents one person's cached business
  // data from appearing after another person signs in on the same browser tab.
  useEffect(() => {
    onSessionExpired(() => {
      setUsername(null);
      query.clear();
    });
    return () => onSessionExpired(() => {});
  }, [query]);
  function signIn(name: string, result: LoginResult) {
    if ('accessToken' in result) {
      acceptTokens(result);
      query.clear();
      setUsername(name);
    }
  }
  async function signOut() {
    try {
      await logout();
    } finally {
      query.clear();
      setUsername(null);
      setShowProof(false);
    }
  }
  return (
    <Context.Provider
      value={{ username, signIn, signOut, reauthenticate: () => setShowProof(true) }}
    >
      {children}
      {showProof && <Reauthenticate close={() => setShowProof(false)} />}
    </Context.Provider>
  );
}
export function useSession() {
  const value = useContext(Context);
  if (!value) throw new Error('SessionProvider is missing');
  return value;
}
function Reauthenticate({ close }: { close: () => void }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>();
  return (
    <Modal title="Confirm it’s you" onClose={close}>
      <p className="muted">
        Sensitive changes need recent authentication. Confirm your password, then retry your change.
      </p>
      <form
        onSubmit={async (event) => {
          event.preventDefault();
          const form = event.currentTarget;
          const data = new FormData(form);
          setBusy(true);
          setError(undefined);
          try {
            const result = await post<{ reauthenticationToken: string }>('/auth/reauthenticate', {
              currentPassword: data.get('password'),
              code: data.get('code') || null,
            });
            setProof(result.reauthenticationToken);
            close();
          } catch (error) {
            setError(error);
          } finally {
            form.reset();
            setBusy(false);
          }
        }}
      >
        <label>
          Password
          <input name="password" type="password" autoComplete="current-password" required />
        </label>
        <label>
          Authenticator or recovery code <span className="muted">(if enabled)</span>
          <input name="code" autoComplete="one-time-code" />
        </label>
        <ErrorNotice error={error} />
        <Button busy={busy}>Confirm identity</Button>
      </form>
    </Modal>
  );
}
