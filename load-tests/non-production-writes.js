import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate } from 'k6/metrics';
import { baseUrl, envBool, envInt, envNumber, envString, requiredEnv } from './lib/env.js';
import { profileOptions } from './lib/profiles.js';

if (!envBool('ALLOW_WRITES', false)) {
    throw new Error('Write scenarios are disabled. Set ALLOW_WRITES=true only for an isolated test environment.');
}
if (envString('TARGET_ENV').toLowerCase() !== 'non-production') {
    throw new Error('TARGET_ENV=non-production is required for write scenarios.');
}

const apiBaseUrl = baseUrl();
const password = requiredEnv('TEST_PASSWORD');
const flow = envString('WRITE_FLOW', 'active-auth').toLowerCase();
const refreshEvery = envInt('REFRESH_EVERY_ITERATIONS', 10, 1);
const thinkTime = envNumber('THINK_TIME_SECONDS', 5, 0);

if (!['active-auth', 'unknown-device-registration'].includes(flow)) {
    throw new Error('WRITE_FLOW must be active-auth or unknown-device-registration.');
}

const activeDeviceId = flow === 'active-auth' ? requiredEnv('DEVICE_ID') : null;
const devicePrefix = flow === 'unknown-device-registration' ? requiredEnv('DEVICE_ID_PREFIX') : null;
const registrationAddress = flow === 'unknown-device-registration' ? requiredEnv('REGISTRATION_ADDRESS') : null;

export const apiErrorRate = new Rate('api_error_rate');
export const options = profileOptions(envString('PROFILE', 'smoke'));

let session = null;
let submittedRegistration = false;
let iterationsSinceRefresh = 0;

function record(response, endpoint, expectedStatuses) {
    const ok = expectedStatuses.includes(response.status);
    apiErrorRate.add(!ok, { endpoint });
    check(response, { [`${endpoint} returned ${expectedStatuses.join(' or ')}`]: () => ok });
    return ok;
}

function jsonHeaders(accessToken) {
    const headers = { 'Content-Type': 'application/json', Accept: 'application/json' };
    if (accessToken) headers.Authorization = `Bearer ${accessToken}`;
    return headers;
}

function login() {
    const deviceId = flow === 'active-auth' ? activeDeviceId : `${devicePrefix}-${__VU}`;
    const response = http.post(`${apiBaseUrl}/api/auth/login`, JSON.stringify({ password, deviceId }), {
        headers: jsonHeaders(), tags: { endpoint: 'auth_login_write' },
    });
    if (!record(response, 'auth_login_write', [200])) return null;

    const body = response.json();
    const expectedRegistration = flow === 'unknown-device-registration';
    if (body.deviceRegistrationRequired !== expectedRegistration) {
        apiErrorRate.add(true, { endpoint: 'auth_login_semantics' });
        throw new Error(`Unexpected deviceRegistrationRequired=${body.deviceRegistrationRequired} for ${flow}.`);
    }
    return { accessToken: body.accessToken, refreshToken: body.refreshToken, deviceId };
}

function refresh() {
    const response = http.post(`${apiBaseUrl}/api/auth/refresh`, JSON.stringify({
        refreshToken: session.refreshToken,
    }), { headers: jsonHeaders(), tags: { endpoint: 'auth_refresh_write' } });
    if (!record(response, 'auth_refresh_write', [200])) return false;
    const body = response.json();
    session.accessToken = body.accessToken;
    session.refreshToken = body.refreshToken;
    return true;
}

export default function () {
    if (!session) {
        session = login();
        if (!session) { sleep(thinkTime); return; }
    }

    if (flow === 'unknown-device-registration') {
        if (!submittedRegistration) {
            const response = http.post(`${apiBaseUrl}/api/device/request`, JSON.stringify({
                deviceId: session.deviceId,
                name: `k6-vu-${__VU}`,
                address: registrationAddress,
                honestFlowVersion: envString('HONESTFLOW_VERSION', 'k6-load-test'),
            }), {
                headers: jsonHeaders(session.accessToken), tags: { endpoint: 'device_request_write' },
            });
            submittedRegistration = record(response, 'device_request_write', [202]);
        }

        const statusResponse = http.get(`${apiBaseUrl}/api/device/registration/current`, {
            headers: jsonHeaders(session.accessToken), tags: { endpoint: 'registration_current' }, responseType: 'none',
        });
        record(statusResponse, 'registration_current', [200]);
    } else {
        let response = http.get(`${apiBaseUrl}/api/configuration/current`, {
            headers: jsonHeaders(session.accessToken), tags: { endpoint: 'configuration_current' }, responseType: 'none',
        });
        record(response, 'configuration_current', [200]);

        response = http.get(`${apiBaseUrl}/api/license/current`, {
            headers: jsonHeaders(session.accessToken), tags: { endpoint: 'license_current' }, responseType: 'none',
        });
        record(response, 'license_current', [200]);
    }

    iterationsSinceRefresh += 1;
    if (iterationsSinceRefresh >= refreshEvery && refresh()) iterationsSinceRefresh = 0;
    if (thinkTime > 0) sleep(thinkTime);
}
