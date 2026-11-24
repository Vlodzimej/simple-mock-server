import { defineConfig } from '@playwright/test';
import path from 'node:path';

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  workers: 1,
  use: { baseURL: 'http://localhost:4201', trace: 'retain-on-failure' },
  webServer: [
    {
      command:
        'dotnet run --project ../src/MockServer.Api --no-launch-profile --urls http://localhost:5081',
      url: 'http://localhost:5081/health',
      env: { DatabasePath: path.resolve('../work/e2e.db') },
      timeout: 60000,
    },
    {
      command:
        'npm exec --package=node@24 -- node node_modules/@angular/cli/bin/ng.js serve --port 4201 --proxy-config proxy.e2e.json',
      url: 'http://localhost:4201',
      timeout: 60000,
    },
  ],
});
