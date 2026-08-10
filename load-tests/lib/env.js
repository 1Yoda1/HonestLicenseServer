export function envString(name, fallback = '') {
    const value = __ENV[name];
    return value === undefined || value === null || value.trim() === ''
        ? fallback
        : value.trim();
}

export function requiredEnv(name) {
    const value = envString(name);
    if (!value) throw new Error(`Environment variable ${name} is required.`);
    return value;
}

export function envBool(name, fallback) {
    const value = envString(name);
    if (!value) return fallback;
    if (value.toLowerCase() === 'true') return true;
    if (value.toLowerCase() === 'false') return false;
    throw new Error(`${name} must be true or false.`);
}

export function envInt(name, fallback, minimum = 0) {
    const value = envString(name);
    if (!value) return fallback;
    const parsed = Number.parseInt(value, 10);
    if (!Number.isInteger(parsed) || parsed < minimum) {
        throw new Error(`${name} must be an integer >= ${minimum}.`);
    }
    return parsed;
}

export function envNumber(name, fallback, minimum = 0) {
    const value = envString(name);
    if (!value) return fallback;
    const parsed = Number(value);
    if (!Number.isFinite(parsed) || parsed < minimum) {
        throw new Error(`${name} must be a number >= ${minimum}.`);
    }
    return parsed;
}

export function baseUrl() {
    return requiredEnv('BASE_URL').replace(/\/+$/, '');
}
