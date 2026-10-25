// Run against a disposable staging API, never a real customer's account.
// Install k6 separately, then: k6 run -e BASE_URL=https://localhost:7045 scripts/auth-rate-load.js
// One shared client IP deliberately exercises the shared-public-IP limit. Run
// from several staging machines separately to assess distinct-client budgets.
import http from 'k6/http';
import { check } from 'k6';
import { Rate } from 'k6/metrics';

const rejected = new Rate('rate_limited');
const base = __ENV.BASE_URL;
if (!base) throw new Error('BASE_URL must point to a disposable staging API');
export const options = {
  scenarios: { shared_ip_burst: { executor: 'shared-iterations', vus: 5, iterations: 30, maxDuration: '30s' } },
  thresholds: { checks: ['rate==1'], http_req_duration: ['p(95)<2000'], rate_limited: ['rate>0'] },
};

export default function () {
  // Never use a real username: repeated bad passwords could lock it out. No
  // registration or recovery requests are sent, so this probe queues no emails.
  const response = http.post(`${base}/auth/login`, JSON.stringify({
    username: 'load-probe-missing', password: 'Invalid-Only-For-Load-593!',
  }), { headers: { 'Content-Type': 'application/json' }, responseType: 'none' });
  rejected.add(response.status === 429);
  check(response, {
    'request is rejected safely': r => r.status === 401 || r.status === 429,
    'throttled request has a positive retry interval': r => r.status !== 429 || Number(r.headers['Retry-After']) > 0,
  });
}
