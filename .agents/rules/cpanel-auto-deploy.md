# cPanel Deployment Rule for Netrunner Games Backend
# Trigger: Whenever AuthNetrunnerGames/index.js, generate_server.js, or server.js is modified.

1. **Security & Secrets**:
   - The cPanel UAPI Token and User must **NEVER** be committed or referenced in tracked files.
   - It is stored strictly in `AuthNetrunnerGames/.env.local` (which is in `.gitignore`).

2. **Compile Standalone Server**:
   - Execute: `node C:\Users\Flynn\Projects\AuthNetrunnerGames\generate_server.js`
   - This inlines `auth_pages.js`, embeds the Neon DB shims, and includes the cache/worker polyfills into `C:\Users\Flynn\Projects\AuthNetrunnerGames\server.js`.

3. **Automated UAPI Upload & Zero-Downtime Reload**:
   - Execute: `node C:\Users\Flynn\Projects\AuthNetrunnerGames\cpanel_deploy.js`
   - This script reads credentials securely from `.env.local`, uploads `server.js` to `/home/netrunga/api/server.js`, and touches `/home/netrunga/api/tmp/restart.txt` to trigger LiteSpeed to reload the app.

4. **Verify Live Status**:
   - Run a GET request against `https://netrungames.site/api/depotbox/api/games/379720/availability` to ensure `200 OK`.
