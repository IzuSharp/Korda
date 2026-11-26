package main

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"strconv"
)

const FrozenManifestSHA256 = "76443b1c88bf011f57b3c9819dc85a8bedb5376121d3ef2670304f96b6d2aae2"

var frozenCaseNames = []string{
	"numeric", "text", "money", "json", "wide", "optional-filter", "range", "left-join", "inner-join", "null-notes", "literal-note", "customer-totals", "product-totals", "empty-sum", "cursor", "offset", "exists", "in-subquery", "cte", "window", "union-all", "union", "intersect", "except", "running-total", "three-join", "anti-association", "nullable-order", "raw-sql", "related", "tx-commit", "tx-rollback", "partial-update", "stale-conflict", "early-stop",
}

type Manifest struct {
	SchemaVersion int `json:"schemaVersion"`
	Dataset       struct {
		Tenants         int `json:"tenants"`
		OrdersPerTenant int `json:"ordersPerTenant"`
		Customers       int `json:"customers"`
		RequestedRows   int `json:"requestedRows"`
		BatchRows       int `json:"batchRows"`
	} `json:"dataset"`
	Queries []Query `json:"queries"`
	SHA256  string  `json:"-"`
}

type Query struct {
	Name                        string            `json:"name"`
	SQL                         map[string]string `json:"sql"`
	Parameters                  []Parameter       `json:"parameters"`
	Columns                     []Column          `json:"columns"`
	ExpectedRows                int64             `json:"expectedRows"`
	ExpectedDigest              string            `json:"expectedDigest"`
	ExpectedDigestHex           string            `json:"expectedDigestHex"`
	QueryExecutionsPerOperation int64             `json:"queryExecutionsPerOperation"`
	Materialization             string            `json:"materialization"`
	ConsumeRows                 int64             `json:"consumeRows"`
	Lifecycle                   *Lifecycle        `json:"lifecycle"`
	args                        []any
	digest                      uint64
}

type Parameter struct {
	Name  string          `json:"name"`
	Kind  string          `json:"kind"`
	Value json.RawMessage `json:"value"`
}

type Column struct {
	Name     string `json:"name"`
	Alias    string `json:"alias"`
	Kind     string `json:"kind"`
	Nullable bool   `json:"nullable"`
}

type Lifecycle struct {
	Kind  string            `json:"kind"`
	Begin map[string]string `json:"begin"`
	Steps []LifecycleStep   `json:"steps"`
}

type LifecycleStep struct {
	Name             string            `json:"name"`
	SQL              map[string]string `json:"sql"`
	Parameters       []Parameter       `json:"parameters"`
	ExpectedAffected *int64            `json:"expectedAffected"`
	ExpectedRows     *int64            `json:"expectedRows"`
	Hash             string            `json:"hash"`
	args             []any
}

func (p Parameter) argument() (any, error) {
	switch p.Kind {
	case "int64":
		var v int64
		if err := json.Unmarshal(p.Value, &v); err != nil {
			return nil, err
		}
		return v, nil
	case "int32":
		var v int32
		if err := json.Unmarshal(p.Value, &v); err != nil {
			return nil, err
		}
		return v, nil
	case "string", "nullable-string":
		if string(p.Value) == "null" {
			if p.Kind != "nullable-string" {
				return nil, fmt.Errorf("non-null string %s is null", p.Name)
			}
			return nil, nil
		}
		var v string
		if err := json.Unmarshal(p.Value, &v); err != nil {
			return nil, err
		}
		return v, nil
	default:
		return nil, fmt.Errorf("unsupported frozen parameter kind %q", p.Kind)
	}
}

func arguments(parameters []Parameter) ([]any, error) {
	values := make([]any, len(parameters))
	for i, parameter := range parameters {
		value, err := parameter.argument()
		if err != nil {
			return nil, fmt.Errorf("parameter %s: %w", parameter.Name, err)
		}
		values[i] = value
	}
	return values, nil
}

// This comparator is deliberately limited to the exact immutable 35-case input.
// Do not silently accept a regenerated SQL/dataset manifest as the same campaign.
func LoadManifest(path string) (*Manifest, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	sum := sha256.Sum256(data)
	identity := hex.EncodeToString(sum[:])
	if identity != FrozenManifestSHA256 {
		return nil, fmt.Errorf("workload manifest identity differs: %s", identity)
	}
	var m Manifest
	if err = json.Unmarshal(data, &m); err != nil {
		return nil, err
	}
	m.SHA256 = identity
	if m.SchemaVersion != 3 || len(m.Queries) != len(frozenCaseNames) || m.Dataset.Tenants != 8 || m.Dataset.OrdersPerTenant != 1024 || m.Dataset.Customers != 256 || m.Dataset.RequestedRows != 32 || m.Dataset.BatchRows != 32 {
		return nil, fmt.Errorf("unexpected frozen dataset or query count")
	}
	for i := range m.Queries {
		q := &m.Queries[i]
		if q.Name != frozenCaseNames[i] || q.SQL["sqlite"] == "" || q.SQL["postgresql"] == "" {
			return nil, fmt.Errorf("invalid frozen query %d", i)
		}
		q.digest, err = strconv.ParseUint(q.ExpectedDigestHex, 16, 64)
		if err != nil {
			return nil, fmt.Errorf("query %s digest: %w", q.Name, err)
		}
		decimalDigest, parseErr := strconv.ParseUint(q.ExpectedDigest, 10, 64)
		if parseErr != nil || decimalDigest != q.digest {
			return nil, fmt.Errorf("query %s digest encodings differ", q.Name)
		}
		q.args, err = arguments(q.Parameters)
		if err != nil {
			return nil, fmt.Errorf("query %s: %w", q.Name, err)
		}
		if q.Lifecycle != nil {
			for j := range q.Lifecycle.Steps {
				step := &q.Lifecycle.Steps[j]
				step.args, err = arguments(step.Parameters)
				if err != nil {
					return nil, fmt.Errorf("query %s step %s: %w", q.Name, step.Name, err)
				}
			}
		}
	}
	return &m, nil
}
