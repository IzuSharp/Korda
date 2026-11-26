package main

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"net/url"
	"path/filepath"
	"slices"
	"strings"
)

type Worker struct {
	ctx                       context.Context
	provider                  string
	db                        *sql.DB
	conn                      *sql.Conn
	manifest                  *Manifest
	statements                []*sql.Stmt
	layoutVerified            []bool
	workerID                  int
	closed                    bool
	closeError                error
	OpenConnectionsAfterClose int
}

// All connection configuration is shared by verification and timed workers.
// PostgreSQL deliberately uses the unnamed exec path, not persistent server
// prepared statements. SQLite reuses its public database/sql statement objects.
func connectionConfig(provider, connection string) (string, string, error) {
	switch provider {
	case "postgresql":
		u, err := url.Parse(connection)
		if err != nil || (u.Scheme != "postgresql" && u.Scheme != "postgres") {
			return "", "", fmt.Errorf("PostgreSQL connection must be an explicit postgres URL")
		}
		p := u.Query()
		p.Set("sslmode", "disable")
		p.Set("default_query_exec_mode", "exec")
		p.Set("statement_cache_capacity", "0")
		p.Set("description_cache_capacity", "0")
		u.RawQuery = p.Encode()
		return "pgx", u.String(), nil
	case "sqlite":
		path, err := filepath.Abs(connection)
		if err != nil {
			return "", "", err
		}
		u := url.URL{Scheme: "file", Path: path}
		p := url.Values{}
		p.Set("_txlock", "immediate")
		p.Set("_foreign_keys", "on")
		p.Set("_journal_mode", "WAL")
		p.Set("_synchronous", "FULL")
		p.Set("_busy_timeout", "5000")
		p.Set("mode", "rw")
		u.RawQuery = p.Encode()
		return "sqlite3", u.String(), nil
	default:
		return "", "", fmt.Errorf("unsupported provider %q", provider)
	}
}

func openPinned(ctx context.Context, provider, connection string) (*sql.DB, *sql.Conn, error) {
	driver, dsn, err := connectionConfig(provider, connection)
	if err != nil {
		return nil, nil, err
	}
	db, err := sql.Open(driver, dsn)
	if err != nil {
		return nil, nil, err
	}
	db.SetMaxOpenConns(1)
	db.SetMaxIdleConns(1)
	conn, err := db.Conn(ctx)
	if err != nil {
		return nil, nil, errors.Join(err, db.Close())
	}
	return db, conn, nil
}

func VerifyDatabase(ctx context.Context, provider, connection string) (info map[string]any, err error) {
	db, conn, err := openPinned(ctx, provider, connection)
	if err != nil {
		return nil, err
	}
	defer func() { err = errors.Join(err, conn.Close(), db.Close()) }()
	var tenants, orders int64
	if err = conn.QueryRowContext(ctx, "SELECT tenants, orders_per_tenant FROM db_perf_metadata").Scan(&tenants, &orders); err != nil {
		return nil, err
	}
	if tenants != 8 || orders != 1024 {
		return nil, oraclef("dataset metadata: expected tenants8/orders1024, got %d/%d", tenants, orders)
	}
	info = map[string]any{"Provider": provider, "Tenants": tenants, "OrdersPerTenant": orders,
		"PersistentConnectionsPerWorker": 1, "PreparedPolicy": "constant SQL and parameter shape reuse; SQLite cached public statements; PostgreSQL unnamed QueryExecModeExec, no server statement or description cache"}
	if provider == "sqlite" {
		var version, journal string
		var synchronous, foreignKeys, busyTimeout int64
		if err = conn.QueryRowContext(ctx, "SELECT sqlite_version()").Scan(&version); err != nil {
			return nil, err
		}
		for _, item := range []struct {
			sql         string
			destination any
		}{
			{"PRAGMA journal_mode", &journal}, {"PRAGMA synchronous", &synchronous},
			{"PRAGMA foreign_keys", &foreignKeys}, {"PRAGMA busy_timeout", &busyTimeout},
		} {
			if err = conn.QueryRowContext(ctx, item.sql).Scan(item.destination); err != nil {
				return nil, err
			}
		}
		if strings.ToLower(journal) != "wal" || synchronous != 2 || foreignKeys != 1 || busyTimeout != 5000 {
			return nil, oraclef("SQLite settings mismatch: journal=%s synchronous=%d foreign_keys=%d busy_timeout=%d", journal, synchronous, foreignKeys, busyTimeout)
		}
		rows, queryErr := conn.QueryContext(ctx, "PRAGMA compile_options")
		if queryErr != nil {
			return nil, queryErr
		}
		options := []string{}
		for rows.Next() {
			var option string
			if err = rows.Scan(&option); err != nil {
				break
			}
			options = append(options, option)
		}
		err = errors.Join(err, rows.Err(), rows.Close())
		if err != nil {
			return nil, err
		}
		slices.Sort(options)
		info["DatabaseVersion"], info["CompileOptions"] = version, options
		info["JournalMode"], info["Synchronous"], info["ForeignKeys"], info["BusyTimeoutMilliseconds"] = journal, synchronous, foreignKeys, busyTimeout
		info["TransactionPolicy"] = "Serializable; driver BEGIN IMMEDIATE; WAL; synchronous FULL; foreign_keys ON; busy_timeout=5000ms"
	} else {
		var version, isolation, readOnly string
		if err = conn.QueryRowContext(ctx, "SELECT version(), current_setting('default_transaction_isolation'), current_setting('default_transaction_read_only')").Scan(&version, &isolation, &readOnly); err != nil {
			return nil, err
		}
		if isolation != "read committed" || readOnly != "off" {
			return nil, oraclef("PostgreSQL transaction defaults mismatch: %s/%s", isolation, readOnly)
		}
		var observed int64
		if err = conn.QueryRowContext(ctx, "SELECT CAST($1 AS bigint)", int64(7)).Scan(&observed); err != nil {
			return nil, err
		}
		if observed != 7 {
			return nil, oraclef("PostgreSQL typed scalar verification failed")
		}
		var named int64
		if err = conn.QueryRowContext(ctx, "SELECT count(*) FROM pg_prepared_statements WHERE name <> ''").Scan(&named); err != nil {
			return nil, err
		}
		if named != 0 {
			return nil, oraclef("PostgreSQL verification retained %d named statements", named)
		}
		info["DatabaseVersion"], info["DefaultIsolation"], info["DefaultReadOnly"] = version, isolation, readOnly
		info["QueryExecMode"], info["StatementCacheCapacity"], info["DescriptionCacheCapacity"] = "exec", 0, 0
		info["NamedPreparedStatementsOnVerificationConnection"] = named
		info["TransactionPolicy"] = "READ COMMITTED, read-write; ordinary database/sql transaction APIs; commit/rollback awaited before persisted-state read"
	}
	return info, nil
}

func NewWorker(ctx context.Context, provider, connection string, manifest *Manifest, workerID int) (*Worker, error) {
	if manifest == nil || len(manifest.Queries) != 35 {
		return nil, fmt.Errorf("worker requires the loaded frozen manifest")
	}
	db, conn, err := openPinned(ctx, provider, connection)
	if err != nil {
		return nil, err
	}
	w := &Worker{ctx: ctx, provider: provider, db: db, conn: conn, manifest: manifest, workerID: workerID,
		statements: make([]*sql.Stmt, len(manifest.Queries)), layoutVerified: make([]bool, len(manifest.Queries))}
	if provider == "sqlite" {
		for i := range manifest.Queries {
			if i >= 30 && i <= 33 {
				continue
			}
			w.statements[i], err = conn.PrepareContext(ctx, manifest.Queries[i].SQL[provider])
			if err != nil {
				return nil, errors.Join(fmt.Errorf("prepare %s: %w", manifest.Queries[i].Name, err), w.Close())
			}
		}
	}
	return w, nil
}

func (w *Worker) Warmup(count int) error {
	for i := range w.manifest.Queries {
		if _, err := w.Execute(int64(i)); err != nil {
			return fmt.Errorf("worker%d verify: %w", w.workerID, err)
		}
	}
	for i := 0; i < count; i++ {
		if _, err := w.Execute(int64(i)); err != nil {
			return fmt.Errorf("worker%d warmup: %w", w.workerID, err)
		}
	}
	return nil
}

func (w *Worker) Execute(index int64) (result OperationResult, err error) {
	if w.closed || index < 0 {
		return result, fmt.Errorf("invalid worker execution state")
	}
	caseIndex := int(index % int64(len(w.manifest.Queries)))
	query := &w.manifest.Queries[caseIndex]
	if caseIndex >= 30 && caseIndex <= 33 {
		result, err = w.transaction(query)
	} else {
		var rows *sql.Rows
		result.SQLExecutions = 1
		if stmt := w.statements[caseIndex]; stmt != nil {
			rows, err = stmt.QueryContext(w.ctx, query.args...)
		} else {
			rows, err = w.conn.QueryContext(w.ctx, query.SQL[w.provider], query.args...)
		}
		if err != nil {
			return result, fmt.Errorf("%s query: %w", query.Name, err)
		}
		if !w.layoutVerified[caseIndex] {
			columns, columnErr := rows.Columns()
			if columnErr != nil {
				return result, errors.Join(columnErr, rows.Close())
			}
			if len(columns) != len(query.Columns) {
				return result, errors.Join(oraclef("%s: expected%d columns, got%d", query.Name, len(query.Columns), len(columns)), rows.Close())
			}
			for i, name := range columns {
				if name != query.Columns[i].Alias {
					return result, errors.Join(oraclef("%s: column%d expected%s, got%s", query.Name, i, query.Columns[i].Alias, name), rows.Close())
				}
			}
			w.layoutVerified[caseIndex] = true
		}
		if caseIndex == 29 {
			result, err = w.related(rows)
		} else {
			result, err = w.consume(rows, caseIndex)
		}
	}
	if err != nil {
		return result, fmt.Errorf("%s: %w", query.Name, err)
	}
	if result.Rows != query.ExpectedRows || result.Digest != query.digest {
		return result, oraclef("%s: expected %d/%016x, observed %d/%016x", query.Name, query.ExpectedRows, query.digest, result.Rows, result.Digest)
	}
	if result.SQLExecutions != query.QueryExecutionsPerOperation {
		return result, oraclef("%s: business SQL/control count expected%d, got%d", query.Name, query.QueryExecutionsPerOperation, result.SQLExecutions)
	}
	return result, nil
}

func retireRows(rows *sql.Rows, err *error) {
	closeErr := rows.Close()
	*err = errors.Join(*err, rows.Err(), closeErr)
}

func (w *Worker) scanOrder(rows *sql.Rows) (order Order, err error) {
	money := Money{postgres: w.provider == "postgresql"}
	err = rows.Scan(&order.TenantId, &order.Id, &order.CreatedAt, &order.CustomerId, &money, &order.Note, &order.Version, &order.Settings, &order.InternalMemo)
	if err == nil && !money.Valid {
		err = oraclef("non-null Order.Total was NULL")
	}
	order.TotalCents = money.Cents
	return order, err
}

func (w *Worker) consume(rows *sql.Rows, caseIndex int) (result OperationResult, err error) {
	result.SQLExecutions = 1
	defer retireRows(rows, &err)
	digest := newDigest()
	stopAfter := int64(1<<63 - 1)
	if caseIndex == 34 {
		stopAfter = 3
	}
	for result.Rows < stopAfter && rows.Next() {
		switch caseIndex {
		case 0, 5, 6, 9, 10, 14, 15, 16, 17, 18, 20, 21, 22, 23, 26, 27:
			var value int64
			if err = rows.Scan(&value); err != nil {
				return result, err
			}
			digest.Integer(value)
		case 1, 28:
			var row TextRow
			if err = rows.Scan(&row.Id, &row.Note); err != nil {
				return result, err
			}
			digest.Integer(row.Id)
			digest.NullableText(row.Note)
		case 2:
			row := MoneyRow{Total: Money{postgres: w.provider == "postgresql"}}
			if err = rows.Scan(&row.Id, &row.Total); err != nil {
				return result, err
			}
			if !row.Total.Valid {
				return result, oraclef("MoneyRow.Total was NULL")
			}
			digest.Integer(row.Id)
			digest.Integer(row.Total.Cents)
		case 3:
			var row JsonRow
			if err = rows.Scan(&row.Id, &row.Settings); err != nil {
				return result, err
			}
			digest.Integer(row.Id)
			digest.Text(row.Settings.Channel)
			digest.Boolean(row.Settings.Gift)
		case 4, 34:
			row, scanErr := w.scanOrder(rows)
			if scanErr != nil {
				return result, scanErr
			}
			digest.Order(row)
		case 7, 8:
			var row JoinRow
			if err = rows.Scan(&row.Id, &row.Name); err != nil {
				return result, err
			}
			digest.Integer(row.Id)
			digest.NullableText(row.Name)
		case 11:
			row := CustomerTotal{Total: Money{postgres: w.provider == "postgresql"}}
			if err = rows.Scan(&row.CustomerId, &row.Count, &row.Total); err != nil {
				return result, err
			}
			digest.NullableInteger(row.CustomerId)
			digest.Integer(row.Count)
			digest.NullableMoney(row.Total)
		case 12:
			var row ProductTotal
			if err = rows.Scan(&row.Product, &row.Quantity); err != nil {
				return result, err
			}
			digest.Text(row.Product)
			digest.NullableInteger(row.Quantity)
		case 13:
			row := Money{postgres: w.provider == "postgresql"}
			if err = rows.Scan(&row); err != nil {
				return result, err
			}
			digest.NullableMoney(row)
		case 19:
			var row RankRow
			if err = rows.Scan(&row.Id, &row.Rank); err != nil {
				return result, err
			}
			digest.Integer(row.Id)
			digest.Integer(row.Rank)
		case 24:
			var row RunningRow
			if err = rows.Scan(&row.Id, &row.Quantity); err != nil {
				return result, err
			}
			digest.Integer(row.Id)
			digest.NullableInteger(row.Quantity)
		case 25:
			var row DetailRow
			if err = rows.Scan(&row.OrderId, &row.Name, &row.Product, &row.Quantity); err != nil {
				return result, err
			}
			digest.Integer(row.OrderId)
			digest.Text(row.Name)
			digest.Text(row.Product)
			digest.Integer(row.Quantity)
		default:
			return result, fmt.Errorf("unexpected stream case index%d", caseIndex)
		}
		result.Rows++
	}
	if err = rows.Err(); err != nil {
		return result, err
	}
	result.Digest, result.SQLExecutions = digest.Value, 1
	return result, nil
}

func (w *Worker) related(rows *sql.Rows) (result OperationResult, err error) {
	result.SQLExecutions = 1
	readerClosed := false
	defer func() {
		if !readerClosed {
			retireRows(rows, &err)
		}
	}()
	// Match buffered-related: own all IndexedLine values before allocating and
	// populating the per-parent child groups, including empty parents.
	buffered := make([]IndexedLine, 0, 32)
	for rows.Next() {
		var row IndexedLine
		if err = rows.Scan(&row.Id, &row.TenantId, &row.OrderId, &row.Product, &row.Quantity, &row.ParentIndex); err != nil {
			return result, err
		}
		buffered = append(buffered, row)
	}
	err = errors.Join(rows.Err(), rows.Close())
	readerClosed = true
	if err != nil {
		return result, err
	}
	groups := make([][]OrderLine, 32)
	for _, row := range buffered {
		if row.ParentIndex < 0 || row.ParentIndex >= 32 {
			return result, oraclef("related: invalid parent ordinal%d", row.ParentIndex)
		}
		groups[row.ParentIndex] = append(groups[row.ParentIndex], row.OrderLine)
	}
	digest := newDigest()
	for index, group := range groups {
		slices.SortFunc(group, func(a, b OrderLine) int {
			if a.Id < b.Id {
				return -1
			}
			if a.Id > b.Id {
				return 1
			}
			return 0
		})
		digest.Integer(int64(index + 1))
		digest.Integer(int64(len(group)))
		for _, row := range group {
			digest.Line(row)
			result.Rows++
		}
	}
	result.Digest, result.SQLExecutions = digest.Value, 1
	return result, nil
}

type orderQueryer interface {
	QueryContext(context.Context, string, ...any) (*sql.Rows, error)
}

func (w *Worker) querySingleOrder(source orderQueryer, statement string, args []any) (order Order, err error) {
	rows, err := source.QueryContext(w.ctx, statement, args...)
	if err != nil {
		return order, err
	}
	defer retireRows(rows, &err)
	if !rows.Next() {
		if err = rows.Err(); err != nil {
			return order, err
		}
		return order, oraclef("expected exactly one full order, got zero")
	}
	if order, err = w.scanOrder(rows); err != nil {
		return order, err
	}
	if rows.Next() {
		return order, oraclef("expected exactly one full order, got more than one")
	}
	if err = rows.Err(); err != nil {
		return order, err
	}
	return order, nil
}

func (w *Worker) transaction(query *Query) (result OperationResult, err error) {
	if query.Lifecycle == nil {
		return result, fmt.Errorf("missing transaction lifecycle")
	}
	isolation := sql.LevelSerializable
	if w.provider == "postgresql" {
		isolation = sql.LevelReadCommitted
	}
	transaction, err := w.conn.BeginTx(w.ctx, &sql.TxOptions{Isolation: isolation, ReadOnly: false})
	result.SQLExecutions++
	if err != nil {
		return result, err
	}
	finished := false
	defer func() {
		if !finished {
			cleanup := transaction.Rollback()
			if !errors.Is(cleanup, sql.ErrTxDone) {
				err = errors.Join(err, cleanup)
			}
		}
	}()
	digest := newDigest()
	var original Order
	// Lifecycle SQL is reused, but parameter containers and the transaction's
	// application values are constructed during each measured operation.
	lookupArgs := append([]any(nil), query.args...)
	for _, step := range query.Lifecycle.Steps {
		statement := step.SQL[w.provider]
		switch step.Name {
		case "find-original", "find-changed", "find-persisted":
			var source orderQueryer = transaction
			if step.Name == "find-persisted" {
				if !finished {
					return result, oraclef("persisted-state read attempted before transaction retirement")
				}
				source = w.conn
			}
			var row Order
			result.SQLExecutions++
			row, err = w.querySingleOrder(source, statement, lookupArgs)
			if err != nil {
				return result, err
			}
			switch step.Name {
			case "find-original":
				original = row
				if original != seedOrder(1024) {
					return result, oraclef("write target differs from fixed full initial state")
				}
			case "find-changed":
				expected := original
				if query.Name == "partial-update" {
					expected.Note = sql.NullString{}
				} else {
					expected.Note = sql.NullString{String: "perf-mutated", Valid: true}
					expected.Version = 2
				}
				if row != expected {
					return result, oraclef("partial mutation changed omitted fields or lost NULL")
				}
			case "find-persisted":
				if row != original {
					return result, oraclef("durable state was not restored after transaction")
				}
			}
			digest.Order(row)
			result.Rows++
		case "change", "stale-change", "restore-before-commit":
			values := append([]any(nil), step.args...)
			var command sql.Result
			result.SQLExecutions++
			command, err = transaction.ExecContext(w.ctx, statement, values...)
			if err != nil {
				return result, err
			}
			affected, affectedErr := command.RowsAffected()
			if affectedErr != nil {
				return result, affectedErr
			}
			if step.ExpectedAffected == nil || affected != *step.ExpectedAffected {
				return result, oraclef("%s affected count: got%d", step.Name, affected)
			}
			if step.Name == "stale-change" {
				digest.Boolean(true)
			}
		case "commit":
			result.SQLExecutions++
			err = transaction.Commit()
			finished = true
			if err != nil {
				return result, err
			}
		case "rollback":
			result.SQLExecutions++
			err = transaction.Rollback()
			finished = true
			if err != nil {
				return result, err
			}
		default:
			return result, fmt.Errorf("unsupported transaction step %s", step.Name)
		}
	}
	if !finished {
		return result, oraclef("transaction did not reach its completion step")
	}
	result.Digest = digest.Value
	return result, nil
}

func (w *Worker) Close() error {
	if w == nil {
		return nil
	}
	if w.closed {
		return w.closeError
	}
	w.closed = true
	var err error
	for _, statement := range w.statements {
		if statement != nil {
			err = errors.Join(err, statement.Close())
		}
	}
	if w.conn != nil {
		err = errors.Join(err, w.conn.Close())
	}
	if w.db != nil {
		err = errors.Join(err, w.db.Close())
		// Read the driver's application pool count after synchronous closure;
		// successful Close alone is not treated as an observed zero count.
		w.OpenConnectionsAfterClose = w.db.Stats().OpenConnections
		if w.OpenConnectionsAfterClose != 0 {
			err = errors.Join(err, oraclef("worker%d retained%d application connections after close", w.workerID, w.OpenConnectionsAfterClose))
		}
	}
	w.closeError = err
	return err
}
