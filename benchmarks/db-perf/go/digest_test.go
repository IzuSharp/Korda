package main

import (
	"math"
	"testing"
)

// Fixed monetary values and Int64 limits exercise the external business
// contract: exact cents, never rounding a numeric value that loses precision.
func TestExactCentsPreservesBusinessValues(t *testing.T) {
	for _, test := range []struct {
		input string
		cents int64
	}{
		{"0", 0}, {"-0.00", 0}, {"389.97", 38997}, {"389.9700", 38997},
		{"-1.23", -123}, {"-0.0100", -1}, {"000001.2000", 120},
		{"1.2300e2", 12300}, {"123e-2", 123},
		{"92233720368547758.07", math.MaxInt64},
		{"-92233720368547758.08", math.MinInt64},
	} {
		got, err := parseExactCents(test.input)
		if err != nil || got != test.cents {
			t.Errorf("money %q: got %d, err=%v; expected %d cents", test.input, got, err, test.cents)
		}
	}
}

func TestExactCentsRejectsPrecisionLossAndOverflow(t *testing.T) {
	for _, input := range []string{
		"0.001", "-0.001", "1.999", "123e-3",
		"92233720368547758.08", "-92233720368547758.09", "1e100",
		"NaN", "Infinity", "", ".", "1.2.3", "1x", "1e", "1/2",
	} {
		if cents, err := parseExactCents(input); err == nil {
			t.Errorf("invalid/non-integral money %q accepted as %d cents", input, cents)
		}
	}
}

func TestMoneyScannerPreservesNullAndStorageUnits(t *testing.T) {
	pg := Money{postgres: true}
	if err := pg.Scan("389.9700"); err != nil || !pg.Valid || pg.Cents != 38997 {
		t.Fatalf("PostgreSQL numeric did not preserve exact cents: %+v, %v", pg, err)
	}
	if err := pg.Scan(nil); err != nil || pg.Valid || pg.Cents != 0 {
		t.Fatalf("NULL retained a previous non-null value: %+v, %v", pg, err)
	}
	if err := pg.Scan("0.00"); err != nil || !pg.Valid || pg.Cents != 0 {
		t.Fatalf("zero became NULL: %+v, %v", pg, err)
	}
	sqlite := Money{}
	if err := sqlite.Scan(int64(38997)); err != nil || !sqlite.Valid || sqlite.Cents != 38997 {
		t.Fatalf("SQLite integer cents were rescaled: %+v, %v", sqlite, err)
	}
	if err := pg.Scan(float64(389.97)); err == nil {
		t.Fatal("binary floating-point money was accepted")
	}
	nullDigest, zeroDigest := newDigest(), newDigest()
	nullDigest.NullableMoney(Money{})
	zeroDigest.NullableMoney(Money{Valid: true})
	// The frozen empty-sum oracle is a single NULL monetary result.
	if nullDigest.Value != 0xaf63bd4c8601b7df || nullDigest.Value == zeroDigest.Value {
		t.Fatal("canonical NULL money does not match frozen oracle or collapsed into zero")
	}
}

func TestSettingsUseBusinessFieldsRatherThanJSONText(t *testing.T) {
	var first, second OrderSettings
	if err := first.Scan([]byte(`{"Channel":"web","Gift":true}`)); err != nil {
		t.Fatal(err)
	}
	if err := second.Scan(`{ "Gift" : true, "Channel" : "web" }`); err != nil {
		t.Fatal(err)
	}
	expected := OrderSettings{Channel: "web", Gift: true}
	if first != expected || second != expected {
		t.Fatalf("JSON field decoding changed: %+v %+v", first, second)
	}
	if err := second.Scan(nil); err == nil {
		t.Fatal("non-null Settings accepted SQL NULL")
	}
}

func TestSeedAndWideDigestMatchIndependentFrozenOracles(t *testing.T) {
	full, early := newDigest(), newDigest()
	for id := int64(1); id <= 32; id++ {
		row := seedOrder(id)
		full.Order(row)
		if id <= 3 {
			early.Order(row)
		}
	}
	// Copied from the immutable cross-runtime queries-v3.json oracle, not
	// calculated by another copy of the Go implementation in this test.
	if full.Value != 0xc164e0de5cac73ca {
		t.Errorf("wide32 digest=%016x", full.Value)
	}
	if early.Value != 0x9734757769760219 {
		t.Errorf("early-stop3 digest=%016x", early.Value)
	}
}
