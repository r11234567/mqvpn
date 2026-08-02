#!/bin/bash
# Quick sanity check for the SNI routing and H2 proxy feature

set -e

echo "=== SNI Routing & H2 Proxy Sanity Check ==="
echo ""

# Check 1: Files exist
echo "✓ Checking if all files exist..."
for file in src/sni_router.{c,h} src/h2_proxy.{c,h} tests/test_sni_router.c tests/test_h2_proxy.c; do
    if [ ! -f "$file" ]; then
        echo "✗ Missing: $file"
        exit 1
    fi
done
echo "  All source files present"

# Check 2: No unsafe functions
echo ""
echo "✓ Checking for unsafe string functions..."
if grep -rn "strcpy\|strcat\|sprintf\|gets\b" src/sni_router.c src/h2_proxy.c; then
    echo "✗ Found unsafe string functions"
    exit 1
fi
echo "  No unsafe functions found"

# Check 3: CMakeLists includes new files
echo ""
echo "✓ Checking CMakeLists.txt..."
if ! grep -q "src/sni_router.c" CMakeLists.txt; then
    echo "✗ sni_router.c not in CMakeLists.txt"
    exit 1
fi
if ! grep -q "src/h2_proxy.c" CMakeLists.txt; then
    echo "✗ h2_proxy.c not in CMakeLists.txt"
    exit 1
fi
echo "  CMakeLists.txt properly configured"

# Check 4: Headers have include guards
echo ""
echo "✓ Checking include guards..."
for header in src/sni_router.h src/h2_proxy.h; do
    if ! grep -q "#ifndef.*_H" "$header"; then
        echo "✗ Missing include guard in $header"
        exit 1
    fi
done
echo "  Include guards present"

# Check 5: Documentation exists
echo ""
echo "✓ Checking documentation..."
if [ ! -f "docs/server-fallback-proxy.md" ]; then
    echo "✗ Documentation missing"
    exit 1
fi
echo "  Documentation present"

# Check 6: CI workflow exists
echo ""
echo "✓ Checking CI workflow..."
if [ ! -f ".github/workflows/sni-h2-proxy-tests.yml" ]; then
    echo "✗ CI workflow missing"
    exit 1
fi
echo "  CI workflow present"

echo ""
echo "=== All checks passed! ==="
echo ""
echo "Next steps:"
echo "1. Wait for CI to complete: gh run watch"
echo "2. Create PR: gh pr create --fill"
echo "3. Review and merge"
