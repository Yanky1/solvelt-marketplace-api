using Microsoft.EntityFrameworkCore.Migrations;

namespace Solvelt.Marketplace.Api.Data.Migrations
{
    /// <summary>
    /// The PostgreSQL-only search objects (decisions M2-02, M2-13): pg_trgm, <c>catalog_tokens_tsvector</c>, the generated weighted <c>document</c> column (comment 'v3') and the two GIN indexes.
    /// A FROZEN copy of <c>SearchSchema.PostgresDdl</c> as it was when InitialCreate was made: a later change to the search schema is a new migration, never an edit here. The database role needs
    /// permission to <c>CREATE EXTENSION pg_trgm</c> (or the extension is created once by a superuser). The generated Up calls this last (see tools/migrations/regenerate.sh).
    /// </summary>
    public partial class InitialCreate
    {
        private static void CreateSearchSchema(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Ddl);

        /// <summary>The tables (with their columns and indexes) are dropped by Down; the function goes here. The extension stays: other databases objects may use it.</summary>
        private static void DropSearchSchema(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP FUNCTION IF EXISTS catalog_tokens_tsvector(text, \"char\");");

        private const string Ddl = """
        CREATE EXTENSION IF NOT EXISTS pg_trgm;
        CREATE OR REPLACE FUNCTION catalog_tokens_tsvector(t text, w "char") RETURNS tsvector LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $fn$
            SELECT setweight(coalesce((SELECT string_agg(quote_literal(u.tok) || ':' || u.ord, ' ')::tsvector FROM unnest(string_to_array(t, ' ')) WITH ORDINALITY AS u(tok, ord) WHERE u.tok <> ''), ''::tsvector), w)
        $fn$;
        DO $$
        BEGIN
            IF coalesce((SELECT col_description('catalog_search'::regclass, a.attnum) FROM pg_attribute a WHERE a.attrelid = 'catalog_search'::regclass AND a.attname = 'document'), '') <> 'v3' THEN
                ALTER TABLE catalog_search DROP COLUMN IF EXISTS document;
                ALTER TABLE catalog_search ADD COLUMN document tsvector GENERATED ALWAYS AS (
                    catalog_tokens_tsvector(title, 'A') || catalog_tokens_tsvector(brand, 'B') || catalog_tokens_tsvector(terms, 'C') || catalog_tokens_tsvector(description, 'D')) STORED;
                COMMENT ON COLUMN catalog_search.document IS 'v3';
            END IF;
        END $$;
        CREATE INDEX IF NOT EXISTS ix_catalog_search_document ON catalog_search USING gin (document);
        CREATE INDEX IF NOT EXISTS ix_catalog_search_text_trgm ON catalog_search USING gin (text gin_trgm_ops);
        """;
    }
}
