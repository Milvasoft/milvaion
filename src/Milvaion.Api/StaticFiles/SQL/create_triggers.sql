
----------------------------------------------------------------------------------------------------------------
-- Auto increment start 21 

DO $$
DECLARE
    seq_record RECORD;
    schema_name TEXT;
    sequence_name TEXT;
BEGIN
    FOR seq_record IN
        SELECT 
            table_name, 
            column_name, 
            pg_get_serial_sequence('"' || table_name || '"', column_name) AS seq_name
        FROM 
            information_schema.columns
        WHERE 
            table_schema = 'public' 
            AND pg_get_serial_sequence('"' || table_name || '"', column_name) IS NOT NULL
    LOOP
        sequence_name := split_part(seq_record.seq_name, '.', 2);
  
        EXECUTE format('ALTER SEQUENCE %s RESTART WITH 21;', sequence_name);
    END LOOP;
END $$;


----------------------------------------------------------------------------------------------------------------
-- Static Id column name in trigger function
----------------------------------------------------------------------------------------------------------------

----------------------------------------------------------------------------------------------------------------
-- Seed modification prevention trigger
CREATE OR REPLACE FUNCTION prevent_seed_modification()
RETURNS TRIGGER AS $$
BEGIN

    IF TG_OP = 'INSERT' THEN
        IF NEW."Id" < 21 THEN
            RAISE EXCEPTION 'Seed records cannot be modified.';
        END IF;
    ELSIF TG_OP = 'UPDATE' THEN
        IF NEW."Id" < 21 THEN
            RAISE EXCEPTION 'Seed records cannot be modified.';
        END IF;
    ELSIF TG_OP = 'DELETE' THEN
        IF OLD."Id" < 21 THEN
            RAISE EXCEPTION 'Seed records cannot be deleted.';
        END IF;
        RETURN OLD;
    END IF;

    RETURN NEW;
	
END;
$$ LANGUAGE plpgsql;


----------------------------------------------------------------------------------------------------------------
-- Add trigger to all tables


DO $$ 
DECLARE
    r RECORD;
BEGIN
    FOR r IN (
        SELECT 
            c.relname AS table_name
        FROM 
            pg_class c
        JOIN 
            pg_namespace n ON n.oid = c.relnamespace
        JOIN 
            pg_attribute a ON a.attrelid = c.oid
        JOIN 
            pg_constraint con ON con.conrelid = c.oid AND con.contype = 'p' -- Primary Key
        WHERE 
            n.nspname = 'public'
            AND a.attnum = ANY(con.conkey) -- Primary key kolonlarını filtrele
            AND a.attidentity IN ('a', 'd') -- Auto-increment identity kolonları
            AND c.relname not in('_MigrationHistory')
    ) LOOP
        -- Trigger ekle
        EXECUTE format('
            CREATE TRIGGER prevent_seed_modification_trigger
            BEFORE INSERT OR UPDATE OR DELETE ON %I
            FOR EACH ROW EXECUTE FUNCTION prevent_seed_modification();', r.table_name);
    END LOOP;
END $$;


----------------------------------------------------------------------------------------------------------------

----------------------------------------------------------------------------------------------------------------
-- Dynamic primary keycolumn name in trigger function
----------------------------------------------------------------------------------------------------------------

----------------------------------------------------------------------------------------------------------------
-- Seed modification prevention trigger

--CREATE OR REPLACE FUNCTION prevent_seed_modification()
--RETURNS TRIGGER AS $$
--DECLARE 
--		pk_column_name TEXT := TG_ARGV[0];
--    	pk_column_value NUMERIC;
--BEGIN

--	-- Dynamically get new value
--    EXECUTE format('SELECT ($1).%I', pk_column_name) INTO pk_column_value USING NEW;
	
--    IF TG_OP = 'INSERT' THEN
--        IF pk_column_value < 21 THEN
--            RAISE EXCEPTION 'Seed records cannot be modified.';
--        END IF;
--    ELSIF TG_OP = 'UPDATE' THEN
--        IF pk_column_value < 21 THEN
--            RAISE EXCEPTION 'Seed records cannot be modified.';
--        END IF;
--    ELSIF TG_OP = 'DELETE' THEN
--		-- Dynamically get old value
--		EXECUTE format('SELECT ($1).%I', pk_column_name) INTO pk_column_value USING OLD;
--        IF pk_column_value < 21 THEN
--            RAISE EXCEPTION 'Seed records cannot be deleted.';
--        END IF;
--    END IF;

--    RETURN NEW; -- RETURN OLD; if TG_OP = 'DELETE'
	
--END;
--$$ LANGUAGE plpgsql;

----------------------------------------------------------------------------------------------------------------
-- Add trigger to all tables
-- Find auto-increment identity columns and add trigger to prevent modification

--DO $$ 
--DECLARE
--    r RECORD;
--BEGIN
--    FOR r IN (
--        SELECT 
--            c.relname AS table_name,
--		    a.attname as column_name
--        FROM 
--            pg_class c
--        JOIN 
--            pg_namespace n ON n.oid = c.relnamespace
--        JOIN 
--            pg_attribute a ON a.attrelid = c.oid
--        JOIN 
--            pg_constraint con ON con.conrelid = c.oid AND con.contype = 'p' -- Primary Key
--        WHERE 
--            n.nspname = 'public'
--            AND a.attnum = ANY(con.conkey) -- Primary key kolonlarını filtrele
--            AND a.attidentity IN ('a', 'd') -- Auto-increment identity kolonları
--    ) LOOP
--        -- Trigger ekle
--        EXECUTE format('
--            CREATE TRIGGER prevent_seed_modification_trigger
--            BEFORE INSERT OR UPDATE OR DELETE ON %I
--            FOR EACH ROW EXECUTE FUNCTION prevent_seed_modification(%I);', r.table_name, r.column_name);
--    END LOOP;
--END $$;



