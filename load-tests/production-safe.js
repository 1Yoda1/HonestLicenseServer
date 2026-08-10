import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate } from 'k6/metrics';
import { baseUrl, envBool, envNumber, envString } from './lib/env.js';
import { profileOptions } from './lib/profiles.js';

const apiBaseUrl = baseUrl();
const accessToken = envString('ACCESS_TOKEN');
const includeAuthenticatedReads = envBool('INCLUDE_AUTH_READS', true);
const includeVersionRead = envBool('INCLUDE_VERSION_READ', true);
const application = envString('APPLICATION', 'HonestFlow');
const requestPause = envNumber('REQUEST_PAUSE_SECONDS', 0.25, 0);
const thinkTime = envNumber('THINK_TIME_SECONDS', 5, 0);

if (includeAuthenticatedReads && !accessToken) {
    throw new Error('ACCESS_TOKEN is required when INCLUDE_AUTH_READS=true. The script never performs login or refresh.');
}
if (!includeAuthenticatedReads && !includeVersionRead) {
    throw new Error('At least one safe read must be enabled.');
}

export const apiErrorRate = new Rate('api_error_rate');
export const options = profileOptions(envString('PROFILE', 'smoke'));

let licenseEtag = null;

function record(response, endpoint, expectedStatuses) {
    const ok = expectedStatuses.includes(response.status);
    apiErrorRate.add(!ok, { endpoint });
    check(response, { [`${endpoint} returned ${expectedStatuses.join(' or ')}`]: () => ok });
}

function pauseBetweenRequests() {
    if (requestPause > 0) sleep(requestPause);
}

function authenticatedHeaders(extraHeaders = {}) {
    return { Authorization: `Bearer ${accessToken}`, Accept: 'application/json', ...extraHeaders };
}

export default function () {
    if (includeVersionRead) {
        const response = http.get(
            `${apiBaseUrl}/api/version/current/${encodeURIComponent(application)}`,
            { tags: { endpoint: 'version_current' }, responseType: 'none' },
        );
        record(response, 'version_current', [200]);
        pauseBetweenRequests();
    }

    if (includeAuthenticatedReads) {
        let response = http.get(`${apiBaseUrl}/api/configuration/current`, {
            headers: authenticatedHeaders(), tags: { endpoint: 'configuration_current' }, responseType: 'none',
        });
        record(response, 'configuration_current', [200]);
        pauseBetweenRequests();

        const licenseHeaders = licenseEtag
            ? authenticatedHeaders({ 'If-None-Match': licenseEtag })
            : authenticatedHeaders();
        response = http.get(`${apiBaseUrl}/api/license/current`, {
            headers: licenseHeaders, tags: { endpoint: 'license_current' }, responseType: 'none',
        });
        record(response, 'license_current', [200, 304]);
        const responseEtag = response.headers.ETag || response.headers.Etag;
        if (response.status === 200 && responseEtag) licenseEtag = responseEtag;
        pauseBetweenRequests();

        response = http.get(`${apiBaseUrl}/api/device/registration/current`, {
            headers: authenticatedHeaders(), tags: { endpoint: 'registration_current' }, responseType: 'none',
        });
        record(response, 'registration_current', [200]);
    }

    if (thinkTime > 0) sleep(thinkTime);
}
