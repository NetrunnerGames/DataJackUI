export default {
  async fetch(request, env) {
    // Handle CORS preflight
    if (request.method === "OPTIONS") {
      return new Response(null, {
        headers: {
          "Access-Control-Allow-Origin": "*",
          "Access-Control-Allow-Methods": "POST, OPTIONS",
          "Access-Control-Allow-Headers": "Content-Type",
        },
      });
    }

    if (request.method !== "POST") {
      return new Response(JSON.stringify({ error: "Method not allowed" }), {
        status: 405,
        headers: { "Content-Type": "application/json" },
      });
    }

    try {
      const data = await request.json();
      const version = data.version || "unknown";
      const eventName = data.event_name || "App Launch";

      // PostHog EU Cloud Endpoint
      const posthogHost = env.POSTHOG_HOST || "https://eu.i.posthog.com";
      const posthogApiKey = env.POSTHOG_API_KEY;

      if (!posthogApiKey) {
        return new Response(JSON.stringify({ error: "POSTHOG_API_KEY environment variable not set" }), {
          status: 500,
          headers: { "Content-Type": "application/json" },
        });
      }

      // Forward launch event to PostHog EU Cloud
      const posthogRes = await fetch(`${posthogHost}/capture/`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify({
          api_key: posthogApiKey,
          event: eventName,
          distinct_id: "anonymous_desktop_user",
          properties: {
            app_version: version,
            project_id: env.POSTHOG_PROJECT_ID || "281781",
            $lib: "DataJackUI-Desktop",
          },
        }),
      });

      return new Response(JSON.stringify({ success: posthogRes.ok }), {
        status: 200,
        headers: {
          "Content-Type": "application/json",
          "Access-Control-Allow-Origin": "*",
        },
      });
    } catch (err) {
      return new Response(JSON.stringify({ error: err.message }), {
        status: 500,
        headers: { "Content-Type": "application/json" },
      });
    }
  },
};
