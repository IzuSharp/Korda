package main

import (
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"strconv"
	"strings"
)

type OperationResult struct {
	Rows          int64
	Digest        uint64
	SQLExecutions int64
}

type OracleError struct{ Message string }

func (e *OracleError) Error() string { return e.Message }
func oraclef(format string, args ...any) error {
	return &OracleError{Message: fmt.Sprintf(format, args...)}
}
func IsOracleFailure(err error) bool { var oracle *OracleError; return errors.As(err, &oracle) }

type Digest struct{ Value uint64 }

func newDigest() Digest       { return Digest{Value: 14695981039346656037} }
func (d *Digest) Byte(v byte) { d.Value = (d.Value ^ uint64(v)) * 1099511628211 }
func (d *Digest) Integer(v int64) {
	n := uint64(v)
	for i := 0; i < 8; i++ {
		d.Byte(byte(n))
		n >>= 8
	}
}
func (d *Digest) Boolean(v bool) {
	if v {
		d.Byte(1)
	} else {
		d.Byte(0)
	}
}
func (d *Digest) Text(v string) {
	d.Integer(int64(len(v)))
	for i := 0; i < len(v); i++ {
		d.Byte(v[i])
	}
}
func (d *Digest) NullableText(v sql.NullString) {
	if v.Valid {
		d.Text(v.String)
	} else {
		d.Integer(-1)
	}
}
func (d *Digest) NullableInteger(v sql.NullInt64) {
	d.Boolean(v.Valid)
	if v.Valid {
		d.Integer(v.Int64)
	}
}
func (d *Digest) NullableMoney(v Money) {
	d.Boolean(v.Valid)
	if v.Valid {
		d.Integer(v.Cents)
	}
}

// Money implements exact numeric-to-cents conversion. No float64 conversion or
// rounding is permitted; SQLite's integer storage is already in cents.
type Money struct {
	Cents    int64
	Valid    bool
	postgres bool
}

func (m *Money) Scan(src any) error {
	m.Cents, m.Valid = 0, false
	if src == nil {
		return nil
	}
	var value int64
	var err error
	switch v := src.(type) {
	case int64:
		value = v
		if m.postgres {
			if v > math.MaxInt64/100 || v < math.MinInt64/100 {
				return fmt.Errorf("numeric money overflows cents")
			}
			value *= 100
		}
	case string:
		if m.postgres {
			value, err = parseExactCents(v)
		} else {
			value, err = strconv.ParseInt(v, 10, 64)
		}
	case []byte:
		if m.postgres {
			value, err = parseExactCents(string(v))
		} else {
			value, err = strconv.ParseInt(string(v), 10, 64)
		}
	default:
		return fmt.Errorf("unexpected money representation %T; exact integer/decimal required", src)
	}
	if err != nil {
		return err
	}
	m.Cents, m.Valid = value, true
	return nil
}

func parseExactCents(text string) (int64, error) {
	original := text
	negative := false
	if len(text) > 0 && (text[0] == '-' || text[0] == '+') {
		negative = text[0] == '-'
		text = text[1:]
	}
	exponent := 0
	if e := strings.IndexAny(text, "eE"); e >= 0 {
		parsed, err := strconv.Atoi(text[e+1:])
		if err != nil || parsed < -100 || parsed > 100 {
			return 0, fmt.Errorf("invalid decimal exponent %q", original)
		}
		exponent, text = parsed, text[:e]
	}
	var magnitude uint64
	fraction, digits := 0, 0
	dot := false
	for i := 0; i < len(text); i++ {
		c := text[i]
		if c == '.' && !dot {
			dot = true
			continue
		}
		if c < '0' || c > '9' {
			return 0, fmt.Errorf("invalid decimal money %q", original)
		}
		digit := uint64(c - '0')
		if magnitude > (math.MaxUint64-digit)/10 {
			return 0, fmt.Errorf("decimal money overflows %q", original)
		}
		magnitude = magnitude*10 + digit
		digits++
		if dot {
			fraction++
		}
	}
	if digits == 0 {
		return 0, fmt.Errorf("empty decimal money %q", original)
	}
	scale := 2 + exponent - fraction
	for ; scale > 0; scale-- {
		if magnitude > math.MaxUint64/10 {
			return 0, fmt.Errorf("money cents overflow %q", original)
		}
		magnitude *= 10
	}
	for ; scale < 0; scale++ {
		if magnitude%10 != 0 {
			return 0, fmt.Errorf("non-integral cents %q", original)
		}
		magnitude /= 10
	}
	limit := uint64(math.MaxInt64)
	if negative {
		limit++
	}
	if magnitude > limit {
		return 0, fmt.Errorf("signed money cents overflow %q", original)
	}
	if negative {
		if magnitude == 1<<63 {
			return math.MinInt64, nil
		}
		return -int64(magnitude), nil
	}
	return int64(magnitude), nil
}

type OrderSettings struct {
	Channel string `json:"Channel"`
	Gift    bool   `json:"Gift"`
}

func (s *OrderSettings) Scan(src any) error {
	*s = OrderSettings{}
	switch v := src.(type) {
	case string:
		return json.Unmarshal([]byte(v), s)
	case []byte:
		return json.Unmarshal(v, s)
	default:
		return fmt.Errorf("unexpected non-null JSON settings representation %T", src)
	}
}

type TextRow struct {
	Id   int64
	Note sql.NullString
}
type MoneyRow struct {
	Id    int64
	Total Money
}
type JsonRow struct {
	Id       int64
	Settings OrderSettings
}
type JoinRow struct {
	Id   int64
	Name sql.NullString
}
type CustomerTotal struct {
	CustomerId sql.NullInt64
	Count      int64
	Total      Money
}
type ProductTotal struct {
	Product  string
	Quantity sql.NullInt64
}
type RankRow struct {
	Id   int64
	Rank int64
}
type RunningRow struct {
	Id       int64
	Quantity sql.NullInt64
}
type DetailRow struct {
	OrderId  int64
	Name     string
	Product  string
	Quantity int64
}
type OrderLine struct {
	Id       int64
	TenantId int64
	OrderId  int64
	Product  string
	Quantity int64
}
type IndexedLine struct {
	OrderLine
	ParentIndex int32
}
type Order struct {
	TenantId     int64
	Id           int64
	CreatedAt    int64
	CustomerId   sql.NullInt64
	TotalCents   int64
	Note         sql.NullString
	Version      int64
	Settings     OrderSettings
	InternalMemo string
}

func (d *Digest) Order(v Order) {
	d.Integer(v.TenantId)
	d.Integer(v.Id)
	d.Integer(v.CreatedAt)
	d.NullableInteger(v.CustomerId)
	d.Integer(v.TotalCents)
	d.NullableText(v.Note)
	d.Integer(v.Version)
	d.Text(v.Settings.Channel)
	d.Boolean(v.Settings.Gift)
	d.Text(v.InternalMemo)
}
func (d *Digest) Line(v OrderLine) {
	d.Integer(v.Id)
	d.Integer(v.TenantId)
	d.Integer(v.OrderId)
	d.Text(v.Product)
	d.Integer(v.Quantity)
}

// Seed formulas are independent of observed database values and match the
// frozen C# dataset. They are used to assert every field of mutation targets.
func seedOrder(id int64) Order {
	customer := sql.NullInt64{}
	if id%7 != 0 {
		customer.Valid = true
		if id%10 < 7 {
			customer.Int64 = 1 + id%8
		} else {
			customer.Int64 = 9 + id%248
		}
	}
	note := sql.NullString{}
	if id%5 != 0 {
		note.Valid = true
		note.String = "note-" + strconv.FormatInt(id%16, 10)
	}
	channel := "store"
	if id%2 == 0 {
		channel = "web"
	}
	memoSize := 32
	if id%17 == 0 {
		memoSize = 2048
	}
	return Order{TenantId: 1, Id: id, CreatedAt: 1700000000 + id/4, CustomerId: customer,
		TotalCents: 100 + (1009+id*37)%100000, Note: note, Version: 1,
		Settings: OrderSettings{Channel: channel, Gift: id%11 == 0}, InternalMemo: strings.Repeat("m", memoSize)}
}
