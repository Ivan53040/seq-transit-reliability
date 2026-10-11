import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// In development, /api is forwarded to the ASP.NET Core API (dotnet run --project src/SeqTransit.Api).
// In AWS, CloudFront routes /api/* to API Gateway, so the page and API share one origin.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5080',
    },
  },
});
