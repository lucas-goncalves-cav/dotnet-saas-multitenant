#!/usr/bin/env bash
#
# Proves tenant isolation against the running stack, on real SQL Server.
#
# The test suite runs against the EF in memory provider, which is enough for
# the filter logic but says nothing about the SQL that is actually generated.
# This script exercises the same boundary through HTTP against SQL Server, so
# a failure here means the translated query is wrong rather than the rule.

set -euo pipefail

API="${API:-http://localhost:8080}"
SUFFIX="$(date +%s)"

fail() {
    echo "FAILED: $1" >&2
    exit 1
}

expect_status() {
    local expected="$1" actual="$2" what="$3"

    if [ "$expected" != "$actual" ]; then
        fail "$what: expected HTTP $expected, got $actual"
    fi

    echo "  ok: $what returned $actual"
}

json_field() {
    # A small extractor, so the script has no dependency on jq.
    grep -o "\"$2\":\"[^\"]*\"" <<<"$1" | head -1 | cut -d'"' -f4
}

register() {
    local slug="$1"

    curl -s -X POST "$API/api/auth/register" \
        -H 'Content-Type: application/json' \
        -d "{\"tenantName\":\"$slug\",\"tenantSlug\":\"$slug\",\"adminName\":\"Admin\",\"adminEmail\":\"admin@$slug.test\",\"adminPassword\":\"correct horse battery staple\"}"
}

echo "Waiting for $API to become ready"
for _ in $(seq 1 60); do
    if curl -sf "$API/health/ready" >/dev/null 2>&1; then
        break
    fi
    sleep 2
done

curl -sf "$API/health/ready" >/dev/null || fail "the API never became ready"

echo "Registering two tenants"
ACME_JSON="$(register "acme-$SUFFIX")"
GLOBEX_JSON="$(register "globex-$SUFFIX")"

ACME_TOKEN="$(json_field "$ACME_JSON" accessToken)"
GLOBEX_TOKEN="$(json_field "$GLOBEX_JSON" accessToken)"

[ -n "$ACME_TOKEN" ] || fail "no token for the first tenant: $ACME_JSON"
[ -n "$GLOBEX_TOKEN" ] || fail "no token for the second tenant: $GLOBEX_JSON"

echo "Creating a customer for the first tenant"
CUSTOMER_JSON="$(curl -s -X POST "$API/api/customers" \
    -H "Authorization: Bearer $ACME_TOKEN" \
    -H 'Content-Type: application/json' \
    -d "{\"name\":\"Acme Customer\",\"email\":\"buyer-$SUFFIX@example.test\"}")"

CUSTOMER_ID="$(json_field "$CUSTOMER_JSON" id)"

[ -n "$CUSTOMER_ID" ] || fail "the customer was not created: $CUSTOMER_JSON"

echo "Checking what the second tenant can reach"

status() {
    curl -s -o /dev/null -w '%{http_code}' "$@"
}

expect_status 404 "$(status -H "Authorization: Bearer $GLOBEX_TOKEN" "$API/api/customers/$CUSTOMER_ID")" \
    "reading another tenant's customer"

expect_status 404 "$(status -X DELETE -H "Authorization: Bearer $GLOBEX_TOKEN" "$API/api/customers/$CUSTOMER_ID")" \
    "deleting another tenant's customer"

expect_status 401 "$(status "$API/api/customers")" \
    "an anonymous request"

LIST="$(curl -s -H "Authorization: Bearer $GLOBEX_TOKEN" "$API/api/customers")"

grep -q '"totalItems":0' <<<"$LIST" || fail "the second tenant saw rows in its listing: $LIST"
echo "  ok: the second tenant's listing is empty"

OWN="$(curl -s -H "Authorization: Bearer $ACME_TOKEN" "$API/api/customers/$CUSTOMER_ID")"

grep -q "$CUSTOMER_ID" <<<"$OWN" || fail "the owning tenant could not read its own customer: $OWN"
echo "  ok: the owning tenant still reads its own customer"

echo "Checking the plan limit"
for index in 2 3 4 5; do
    curl -s -o /dev/null -X POST "$API/api/users" \
        -H "Authorization: Bearer $ACME_TOKEN" \
        -H 'Content-Type: application/json' \
        -d "{\"name\":\"User $index\",\"email\":\"user$index@acme-$SUFFIX.test\",\"password\":\"correct horse battery staple\",\"role\":\"Member\"}"
done

expect_status 402 "$(status -X POST "$API/api/users" \
    -H "Authorization: Bearer $ACME_TOKEN" \
    -H 'Content-Type: application/json' \
    -d "{\"name\":\"User 6\",\"email\":\"user6@acme-$SUFFIX.test\",\"password\":\"correct horse battery staple\",\"role\":\"Member\"}")" \
    "the sixth user on the free plan"

echo
echo "All checks passed against $API"
