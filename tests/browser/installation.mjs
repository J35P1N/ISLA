import { spawn, execFileSync } from 'node:child_process';
import { mkdtemp, cp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { resolve, join } from 'node:path';
import { randomBytes } from 'node:crypto';
import { once } from 'node:events';

const root = resolve(import.meta.dirname, '../..');
const image = process.env.ISLA_TEST_IMAGE;
const platform = process.env.ISLA_TEST_PLATFORM ?? 'linux/amd64';
const compose = process.env.ISLA_TEST_COMPOSE === '1';
const docker = (...args) => {
  try { return execFileSync('docker', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], timeout: 60_000 }).trim(); }
  catch (error) {
    // Node's default exec error includes the complete command, including secrets.
    const token = args.find(arg => arg.startsWith('Setup__Token='))?.slice('Setup__Token='.length);
    const diagnostic = String(error.stderr ?? error.code ?? 'command failed');
    throw new Error(`Docker ${args[0]} failed: ${token ? diagnostic.replaceAll(token, '[redacted]') : diagnostic}`);
  }
};
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));

// Every resource belongs to this test run. No developer deployment or volume is used.
export class Installation {
  token = randomBytes(32).toString('hex');
  name = `isla-acceptance-${randomBytes(8).toString('hex')}`;
  logs = '';
  get volume() { return compose ? `${this.name}_recipe-data` : this.name; }
  async init() {
    this.directory = await mkdtemp(join(tmpdir(), 'isla-acceptance-'));
    this.data = join(this.directory, 'data');
    if (image) {
      const labels = compose ? ['--label', `com.docker.compose.project=${this.name}`, '--label', 'com.docker.compose.volume=recipe-data'] : [];
      docker('volume', 'create', ...labels, this.volume);
    }
    return this;
  }
  async start({ provider, mountedProvider, token = this.token } = {}) {
    this.options = { provider, mountedProvider, token };
    this.logs = '';
    if (image) {
      const args = ['run', '-d', '--name', this.name, '--platform', platform, '-p', '127.0.0.1::8080',
        '-v', `${this.volume}:/app/data`, '-e', `Setup__Token=${token}`, '-e', 'ASPNETCORE_ENVIRONMENT=Production'];
      if (mountedProvider) {
        const configurationFile = join(this.directory, 'operator.json');
        await writeFile(configurationFile, JSON.stringify({ Storage: { Provider: mountedProvider } }), { mode: 0o444 });
        args.push('-v', `${configurationFile}:/run/isla-operator.json:ro`, '-e', 'ISLA_CONFIG_FILE=/run/isla-operator.json');
      }
      if (provider) args.push('-e', `Storage__Provider=${provider}`);
      if (compose) {
        // Override only image, token, platform and port; retain the repository's
        // service and named-volume wiring. No normal installation is involved.
        const environment = { Setup__Token: token };
        if (provider) environment.Storage__Provider = provider;
        if (mountedProvider) environment.ISLA_CONFIG_FILE = '/run/isla-operator.json';
        const configurationFile = join(this.directory, 'compose.acceptance.yaml');
        const configMount = mountedProvider
          ? `    volumes: [${JSON.stringify(`${join(this.directory, 'operator.json')}:/run/isla-operator.json:ro`)}]\n` : '';
        await writeFile(configurationFile,
          `services:\n  isla:\n    image: ${JSON.stringify(image)}\n    platform: ${JSON.stringify(platform)}\n    container_name: ${JSON.stringify(this.name)}\n    ports: !override ["127.0.0.1::8080"]\n    environment: ${JSON.stringify(environment)}\n${configMount}`,
          { mode: 0o600 });
        this.composeArgs = ['compose', '-p', this.name, '-f', join(root, 'compose.yaml'), '-f', configurationFile];
        docker(...this.composeArgs, 'up', '-d', '--no-build', '--pull', 'never');
      } else docker(...args, image);
      this.url = `http://${docker('port', this.name, '8080/tcp')}`;
    } else {
      const env = { ...process.env, DataDirectory: this.data, Setup__Token: token,
        ASPNETCORE_ENVIRONMENT: 'Development' };
      for (const key of ['Storage__Provider', 'ISLA_CONFIG_FILE', 'Setup__TokenFile']) delete env[key];
      if (mountedProvider) {
        env.ISLA_CONFIG_FILE = join(this.directory, 'operator.json');
        await writeFile(env.ISLA_CONFIG_FILE, JSON.stringify({ Storage: { Provider: mountedProvider } }), { mode: 0o400 });
      }
      if (provider) env.Storage__Provider = provider;
      this.process = spawn('dotnet', [resolve(root, 'src/ISLA/bin/Release/net10.0/ISLA.dll'), '--urls=http://127.0.0.1:0'],
        { env, cwd: resolve(root, 'src/ISLA'), stdio: ['ignore', 'pipe', 'pipe'] });
      this.process.stdout.on('data', chunk => {
        this.logs += chunk;
        const match = this.logs.match(/Now listening on: (http:\/\/[^\s]+)/);
        if (match) this.url = match[1];
      });
      this.process.stderr.on('data', chunk => { this.logs += chunk; });
    }
    const deadline = Date.now() + 30_000;
    while (Date.now() < deadline) {
      if (this.process?.exitCode != null) throw new Error(`Application exited: ${this.logs}`);
      if (this.url) {
        try { const response = await fetch(`${this.url}/setup`); if (response.status === 200) return; } catch { }
      }
      await pause(100);
    }
    throw new Error(`Application failed to start: ${image ? docker('logs', this.name) : this.logs}`);
  }
  readLogs() {
    if (image) this.logs = docker('logs', this.name);
    return this.logs;
  }
  async stop() {
    if (image) {
      this.logs = docker('logs', this.name);
      docker('rm', '-f', this.name);
    } else if (this.process) {
      const exited = once(this.process, 'exit');
      this.process.kill('SIGTERM');
      await exited;
      this.process = undefined;
    }
    if (process.env.ISLA_TEST_DEBUG) console.log(this.logs.replaceAll(this.token, '[redacted]'));
    this.url = undefined;
  }
  async restart() { await this.stop(); await this.start(this.options); }
  async copyTo(other) {
    // Call only with both applications stopped. Preserve account files and DP keys.
    if (image) docker('run', '--rm', '--platform', platform, '--user', '0', '--entrypoint', 'sh',
      '-v', `${this.volume}:/source:ro`, '-v', `${other.volume}:/destination`, image,
      '-c', 'cp -a /source/. /destination/');
    else await cp(this.data, other.data, { recursive: true, preserveTimestamps: true });
  }
  async dispose() {
    if (image) {
      try { await this.stop(); } catch { }
      if (this.composeArgs) docker(...this.composeArgs, 'down');
      docker('volume', 'rm', this.volume);
    } else if (this.process) await this.stop();
    await rm(this.directory, { recursive: true, force: true });
  }
}
