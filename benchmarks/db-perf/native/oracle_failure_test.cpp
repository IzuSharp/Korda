// Compile with the same compiler/driver archives as main.cpp. This bounded
// negative control uses actual libpq result objects without opening a server.
#define main db_perf_consumer_main
#include "main.cpp"
#undef main

static void require_test(bool value, const char* message)
{ if (!value) throw std::runtime_error(message); }

int main()
{
    try
    {
        oracle_phase.store("native-oracle-negative-control");
        for (auto status : {PGRES_COMMAND_OK, PGRES_TUPLES_OK, PGRES_SINGLE_TUPLE, PGRES_EMPTY_QUERY,
             PGRES_COPY_IN, PGRES_COPY_OUT, PGRES_COPY_BOTH, PGRES_NONFATAL_ERROR,
             PGRES_BAD_RESPONSE, PGRES_PIPELINE_SYNC, PGRES_PIPELINE_ABORTED})
        {
            PgResult result(PQmakeEmptyPGresult(nullptr, status));
            require_test(result.value != nullptr, "Cannot allocate libpq negative-control result");
            auto before = oracle_failures.load(); bool rejected = false;
            try { unexpected_pg_result(result.value, "unexpected result at statement terminal"); }
            catch (const OracleViolation& error)
            {
                rejected = true;
                require_test(std::string_view(error.what()).find(PQresStatus(status)) != std::string_view::npos,
                    "Oracle diagnostic lost the raw PostgreSQL status");
                require_test(std::string_view(error.what()).find("; error=") != std::string_view::npos,
                    "Oracle diagnostic lost the PostgreSQL error field");
            }
            require_test(rejected && oracle_failures.load() == before + 1,
                "Illegal PostgreSQL result sequence entered the operational error budget");
        }
        {
            // SQLSTATE cancellation, lock/serialization failures and other
            // ordinary server query errors all use PGRES_FATAL_ERROR.
            PgResult result(PQmakeEmptyPGresult(nullptr, PGRES_FATAL_ERROR));
            require_test(result.value != nullptr, "Cannot allocate libpq server-error control");
            auto before = oracle_failures.load(); bool operational = false;
            try { unexpected_pg_result(result.value, "server query error"); }
            catch (const OracleViolation&) { throw std::runtime_error("Ordinary PostgreSQL server error became an oracle violation"); }
            catch (const std::runtime_error& error)
            {
                operational = true;
                require_test(std::string_view(error.what()).find("PGRES_FATAL_ERROR") != std::string_view::npos,
                    "Operational diagnostic lost the PostgreSQL status");
            }
            require_test(operational && oracle_failures.load() == before, "Server error changed sticky oracle count");
        }
        auto clean_before = oracle_failures.load();
        require_sqlite_retired(SQLITE_OK, SQLITE_OK);
        require_test(oracle_failures.load() == clean_before, "Successful SQLite retirement changed oracle count");
        for (auto statuses : {std::array{SQLITE_BUSY, SQLITE_OK}, std::array{SQLITE_INTERRUPT, SQLITE_OK},
             std::array{SQLITE_OK, SQLITE_MISUSE}, std::array{SQLITE_ERROR, SQLITE_MISUSE}})
        {
            auto before = oracle_failures.load(); bool rejected = false;
            try { require_sqlite_retired(statuses[0], statuses[1]); }
            catch (const OracleViolation& error)
            {
                rejected = true;
                std::string_view message(error.what());
                require_test(message.find("reset=" + std::to_string(statuses[0])) != std::string_view::npos
                    && message.find("clear-bindings=" + std::to_string(statuses[1])) != std::string_view::npos,
                    "SQLite retirement diagnostic lost either raw return code");
            }
            require_test(rejected && oracle_failures.load() == before + 1,
                "Unproven SQLite statement retirement entered the operational error budget");
        }
        auto first = oracle_first(); auto failures = oracle_failures.load();
        oracle_phase.store("subsequent-clean-phase"); require_sqlite_retired(SQLITE_OK, SQLITE_OK);
        bool refused = false;
        try { require_clean_oracle(); } catch (const std::runtime_error&) { refused = true; }
        require_test(refused && failures == 15 && oracle_failures.load() == failures && oracle_first() == first,
            "A later successful phase hid the first native oracle failure");
        require_test(live_pg_results.load() == 0, "A PostgreSQL result owner survived the negative control");
        std::cout << "PASS 11 illegal PostgreSQL statuses, ordinary server-error isolation, 4 SQLite retirement failures, sticky phase isolation, zero result owners\n";
        return 0;
    }
    catch (const std::exception& error)
    { std::cerr << "FAIL native oracle negative control: " << error.what() << '\n'; return 1; }
}
