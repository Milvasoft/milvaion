-- ================================================
-- Search Performance Optimization Indexes
-- ================================================

-- Enable pg_trgm extension for trigram matching (case-insensitive text search)
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- 11. GIN index for AllowedNotifications search
CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Users_AllowedNotifications"
ON "Users" USING gin ("AllowedNotifications");


-- ================================================
-- Performance optimization statistics update
-- ================================================
ANALYZE "Users";