import { envInt, envNumber, envString } from './env.js';

function commonOptions() {
    const failedRate = envNumber('HTTP_REQ_FAILED_RATE', 0.01, 0);
    const p95 = envInt('P95_MS', 500, 1);
    const p99 = envInt('P99_MS', 1000, 1);

    return {
        thresholds: {
            http_req_failed: [`rate<${failedRate}`],
            http_req_duration: [`p(95)<${p95}`, `p(99)<${p99}`],
            api_error_rate: [`rate<${failedRate}`],
        },
        summaryTrendStats: ['avg', 'min', 'med', 'max', 'p(90)', 'p(95)', 'p(99)'],
        userAgent: envString('USER_AGENT', 'HonestLicenseServer-k6/1.0'),
    };
}

export function profileOptions(profileName) {
    const common = commonOptions();

    switch (profileName.toLowerCase()) {
        case 'smoke':
            return { ...common, stages: [
                { duration: envString('SMOKE_RAMP_UP', '10s'), target: envInt('SMOKE_START_VUS', 1, 1) },
                { duration: envString('SMOKE_HOLD', '30s'), target: envInt('SMOKE_MAX_VUS', 5, 1) },
                { duration: envString('SMOKE_RAMP_DOWN', '10s'), target: 0 },
            ] };
        case 'normal-load':
            return { ...common, stages: [
                { duration: envString('NORMAL_STAGE_1_DURATION', '1m'), target: envInt('NORMAL_STAGE_1_VUS', 25, 1) },
                { duration: envString('NORMAL_STAGE_2_DURATION', '2m'), target: envInt('NORMAL_STAGE_2_VUS', 50, 1) },
                { duration: envString('NORMAL_STAGE_3_DURATION', '2m'), target: envInt('NORMAL_STAGE_3_VUS', 100, 1) },
                { duration: envString('NORMAL_RAMP_DOWN', '1m'), target: 0 },
            ] };
        case 'stress':
            return { ...common, stages: [
                { duration: envString('STRESS_STAGE_1_DURATION', '1m'), target: envInt('STRESS_STAGE_1_VUS', 100, 1) },
                { duration: envString('STRESS_STAGE_2_DURATION', '2m'), target: envInt('STRESS_STAGE_2_VUS', 200, 1) },
                { duration: envString('STRESS_STAGE_3_DURATION', '2m'), target: envInt('STRESS_STAGE_3_VUS', 300, 1) },
                { duration: envString('STRESS_STAGE_4_DURATION', '2m'), target: envInt('STRESS_STAGE_4_VUS', 500, 1) },
                { duration: envString('STRESS_RAMP_DOWN', '1m'), target: 0 },
            ] };
        case 'spike':
            return { ...common, stages: [
                { duration: envString('SPIKE_BASE_DURATION', '30s'), target: envInt('SPIKE_BASE_VUS', 10, 1) },
                { duration: envString('SPIKE_RAMP_UP', '10s'), target: envInt('SPIKE_PEAK_VUS', 300, 1) },
                { duration: envString('SPIKE_HOLD', '1m'), target: envInt('SPIKE_PEAK_VUS', 300, 1) },
                { duration: envString('SPIKE_RAMP_DOWN', '10s'), target: envInt('SPIKE_BASE_VUS', 10, 1) },
                { duration: envString('SPIKE_RECOVERY', '30s'), target: envInt('SPIKE_BASE_VUS', 10, 1) },
                { duration: '10s', target: 0 },
            ] };
        case 'soak':
            return { ...common, vus: envInt('SOAK_VUS', 50, 1), duration: envString('SOAK_DURATION', '2h') };
        default:
            throw new Error(`Unknown PROFILE '${profileName}'. Use smoke, normal-load, stress, spike, or soak.`);
    }
}
