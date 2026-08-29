import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { describe, it, expect } from 'vitest';

/**
 * Supply-chain and cross-platform guard for the auth (MSAL) packages introduced by the
 * Entra work.
 *
 * This test fails the build if any auth package regresses to non-`sha512` integrity or a
 * non-canonical registry URL. Because the baseline is now clean, it also holds the ENTIRE
 * lockfile to `sha512` so no future dependency can sneak in with a weaker hash.
 *
 * It also protects the Linux CI install graph. npm 11.6.2 on Windows rewrites this lockfile
 * by marking `@azure/msal-browser` as a peer edge and pruning the top-level optional peer
 * `@emnapi/*` entries that Linux `npm ci` requires. The MSAL peer marker is harmless by
 * itself when the root dependency remains, but in this npm rewrite it is a reliable signal
 * that the lockfile was regenerated on Windows and is no longer cross-platform.
 */

const here = dirname(fileURLToPath(import.meta.url));
const lockPath = resolve(here, '..', '..', 'package-lock.json');

interface LockPackage {
  version?: string;
  resolved?: string;
  integrity?: string;
  peer?: boolean;
  optional?: boolean;
  dev?: boolean;
  link?: boolean;
  dependencies?: Record<string, string>;
}

interface Lockfile {
  lockfileVersion: number;
  packages: Record<string, LockPackage>;
}

const lock = JSON.parse(readFileSync(lockPath, 'utf8')) as Lockfile;

/** Auth packages that must be normal, sha512-pinned direct/transitive dependencies. */
const AUTH_PACKAGES = [
  'node_modules/@azure/msal-browser',
  'node_modules/@azure/msal-common',
  'node_modules/@azure/msal-react',
] as const;

/** Top-level optional peer packages that Linux `npm ci` requires to validate the lockfile. */
const CROSS_PLATFORM_OPTIONAL_PEERS = [
  'node_modules/@emnapi/core',
  'node_modules/@emnapi/runtime',
] as const;

const CANONICAL_REGISTRY = 'https://registry.npmjs.org/';

describe('package-lock.json auth supply-chain integrity', () => {
  it('uses lockfile v3', () => {
    expect(lock.lockfileVersion).toBe(3);
  });

  it.each(AUTH_PACKAGES)('%s resolves canonically with sha512 integrity', (key) => {
    const pkg = lock.packages[key];
    expect(pkg, `${key} must be present in the lockfile`).toBeDefined();
    expect(pkg.integrity, `${key} must have an integrity hash`).toBeTruthy();
    expect(
      pkg.integrity!.startsWith('sha512-'),
      `${key} integrity must be sha512, got: ${pkg.integrity}`,
    ).toBe(true);
    expect(
      pkg.resolved?.startsWith(CANONICAL_REGISTRY),
      `${key} must resolve from the official registry, got: ${pkg.resolved}`,
    ).toBe(true);
  });

  it('keeps the cross-platform MSAL lockfile shape', () => {
    const root = lock.packages[''];
    expect(root.dependencies).toBeDefined();
    expect(
      Object.prototype.hasOwnProperty.call(root.dependencies!, '@azure/msal-browser'),
      '@azure/msal-browser must be a declared direct dependency of the app',
    ).toBe(true);

    const browser = lock.packages['node_modules/@azure/msal-browser'];
    expect(
      browser.peer,
      '@azure/msal-browser peer metadata indicates this lockfile was rewritten on Windows',
    ).not.toBe(true);
  });

  it.each(CROSS_PLATFORM_OPTIONAL_PEERS)('%s stays in the cross-platform lockfile', (key) => {
    const pkg = lock.packages[key];
    expect(
      pkg,
      `${key} must stay in package-lock.json because Linux npm ci validates it`,
    ).toBeDefined();
    expect(pkg.optional, `${key} must remain an optional dependency entry`).toBe(true);
    expect(pkg.peer, `${key} must remain a peer dependency entry`).toBe(true);
  });

  it('rejects any non-sha512 integrity anywhere in the lockfile', () => {
    const offenders = Object.entries(lock.packages)
      .filter(([, p]) => typeof p.integrity === 'string')
      .filter(([, p]) => !p.integrity!.startsWith('sha512-'))
      .map(([name, p]) => `${name || '<root>'}: ${p.integrity}`);

    expect(offenders, `non-sha512 integrity is not allowed:\n${offenders.join('\n')}`).toEqual([]);
  });

  it('rejects any non-canonical (proxy) resolution for resolved tarballs', () => {
    const offenders = Object.entries(lock.packages)
      .filter(([, p]) => typeof p.resolved === 'string')
      .filter(([, p]) => !p.resolved!.startsWith(CANONICAL_REGISTRY))
      .map(([name, p]) => `${name || '<root>'}: ${p.resolved}`);

    expect(offenders, `non-canonical resolve URLs are not allowed:\n${offenders.join('\n')}`).toEqual([]);
  });
});
