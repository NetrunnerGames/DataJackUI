-- ====================================================================
-- Supabase Schema for DataJackUI User Entitlements & Access Control
-- Table: user_entitlements
-- Key: discord_id (Primary)
-- ====================================================================

CREATE TABLE IF NOT EXISTS public.user_entitlements (
    discord_id TEXT PRIMARY KEY,
    username TEXT,
    email TEXT,
    ip_address TEXT,
    is_banned BOOLEAN DEFAULT FALSE,
    tier TEXT DEFAULT 'single', -- 'premium' (lifetime all games) or 'single' ($1 / 100 INR per title)
    allowed_appids BIGINT[] DEFAULT '{}',
    guild_ids TEXT[] DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT NOW(),
    last_login TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

-- Index for IP address fallback lookup
CREATE INDEX IF NOT EXISTS idx_user_entitlements_ip ON public.user_entitlements(ip_address);

-- Enable Row Level Security (RLS)
ALTER TABLE public.user_entitlements ENABLE ROW LEVEL SECURITY;

-- Allow anonymous read policy for client queries
CREATE POLICY "Allow anon select on user_entitlements"
    ON public.user_entitlements
    FOR SELECT
    TO anon
    USING (true);

-- Allow service role full management policy for Auth Worker
CREATE POLICY "Allow service_role insert/update on user_entitlements"
    ON public.user_entitlements
    FOR ALL
    TO service_role
    USING (true)
    WITH CHECK (true);

-- Sample row for a single-title purchaser ($1 / 100 INR per game):
-- INSERT INTO public.user_entitlements (discord_id, username, is_banned, tier, allowed_appids)
-- VALUES ('1553794813411721216', 'flynn', false, 'single', ARRAY[730, 1091500]);

-- Sample row for a lifetime premium user:
-- INSERT INTO public.user_entitlements (discord_id, username, is_banned, tier, allowed_appids)
-- VALUES ('1553794813411721216', 'flynn', false, 'premium', '{}');
