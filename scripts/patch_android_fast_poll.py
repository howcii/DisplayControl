"""
Patch the recovered Android V1.5 APK into a faster-polling compatibility build.

Why this exists:
- The recovered APK has no source code in this repository.
- Its foreground Agent service sleeps about 15 seconds after an empty poll,
  which makes web commands feel slow.
- This script changes only that poll sleep constant and re-signs the APK with
  a local debug certificate using the legacy JAR/APK v1 signature format.

Limitations:
- This does not add real Android system sleep/lock support. The recovered APK
  implements "standby" as a black overlay Activity.
- Some modern Android builds may reject APKs that only have a v1 signature. If
  that happens, the Android Agent must be rebuilt with a proper Android toolchain.
"""

from __future__ import annotations

import argparse
import base64
import datetime as dt
import hashlib
import io
import textwrap
import zipfile
import zlib
from pathlib import Path

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.hazmat.primitives.serialization import pkcs7
from cryptography.x509.oid import NameOID


ORIGINAL_SLEEP_BYTES = bytes.fromhex("1602983a")  # const-wide/16 v2, 15000


def dex_with_updated_header(dex: bytes) -> bytes:
    patched = bytearray(dex)
    signature = hashlib.sha1(patched[32:]).digest()
    patched[12:32] = signature
    checksum = zlib.adler32(patched[12:]) & 0xFFFFFFFF
    patched[8:12] = checksum.to_bytes(4, "little")
    return bytes(patched)


def patch_poll_interval(dex: bytes, interval_ms: int) -> bytes:
    if not 250 <= interval_ms <= 15000:
        raise ValueError("interval_ms must be between 250 and 15000")
    matches = []
    start = 0
    while True:
        index = dex.find(ORIGINAL_SLEEP_BYTES, start)
        if index < 0:
            break
        matches.append(index)
        start = index + 1
    if len(matches) != 1:
        raise RuntimeError(f"expected exactly one poll sleep constant, found {len(matches)}")

    replacement = b"\x16\x02" + interval_ms.to_bytes(2, "little", signed=False)
    patched = dex[: matches[0]] + replacement + dex[matches[0] + len(replacement) :]
    return dex_with_updated_header(patched)


def manifest_digest(data: bytes) -> str:
    return base64.b64encode(hashlib.sha256(data).digest()).decode("ascii")


def wrap_manifest_line(line: str) -> bytes:
    encoded = line.encode("utf-8")
    if len(encoded) <= 72:
        return encoded + b"\r\n"

    chunks = []
    first = True
    remaining = encoded
    while remaining:
        limit = 72 if first else 71
        chunk = remaining[:limit]
        remaining = remaining[limit:]
        chunks.append((b"" if first else b" ") + chunk + b"\r\n")
        first = False
    return b"".join(chunks)


def build_manifest(entries: list[tuple[str, bytes]]) -> tuple[bytes, list[bytes]]:
    sections: list[bytes] = []
    output = io.BytesIO()
    output.write(wrap_manifest_line("Manifest-Version: 1.0"))
    output.write(wrap_manifest_line("Created-By: DisplayControl Codex recovery"))
    output.write(b"\r\n")

    for name, data in entries:
        section = io.BytesIO()
        section.write(wrap_manifest_line(f"Name: {name}"))
        section.write(wrap_manifest_line(f"SHA-256-Digest: {manifest_digest(data)}"))
        section.write(b"\r\n")
        section_bytes = section.getvalue()
        sections.append(section_bytes)
        output.write(section_bytes)

    return output.getvalue(), sections


def build_signature_file(manifest: bytes, sections: list[bytes]) -> bytes:
    output = io.BytesIO()
    output.write(wrap_manifest_line("Signature-Version: 1.0"))
    output.write(wrap_manifest_line("Created-By: DisplayControl Codex recovery"))
    output.write(wrap_manifest_line(f"SHA-256-Digest-Manifest: {manifest_digest(manifest)}"))
    output.write(b"\r\n")

    for section in sections:
        name_line = section.split(b"\r\n", 1)[0].decode("utf-8")
        output.write(wrap_manifest_line(name_line))
        output.write(wrap_manifest_line(f"SHA-256-Digest: {manifest_digest(section)}"))
        output.write(b"\r\n")

    return output.getvalue()


def create_debug_certificate() -> tuple[rsa.RSAPrivateKey, x509.Certificate]:
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    subject = issuer = x509.Name(
        [
            x509.NameAttribute(NameOID.COMMON_NAME, "DisplayControl Android Fast Poll Debug"),
            x509.NameAttribute(NameOID.ORGANIZATION_NAME, "DisplayControl"),
        ]
    )
    now = dt.datetime.now(dt.timezone.utc)
    cert = (
        x509.CertificateBuilder()
        .subject_name(subject)
        .issuer_name(issuer)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now - dt.timedelta(days=1))
        .not_valid_after(now + dt.timedelta(days=3650))
        .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
        .sign(key, hashes.SHA256())
    )
    return key, cert


def sign_sf(sf_bytes: bytes) -> bytes:
    key, cert = create_debug_certificate()
    return (
        pkcs7.PKCS7SignatureBuilder()
        .set_data(sf_bytes)
        .add_signer(cert, key, hashes.SHA256())
        .sign(serialization.Encoding.DER, [pkcs7.PKCS7Options.Binary])
    )


def build_fast_apk(source: Path, destination: Path, interval_ms: int) -> None:
    unsigned_entries: list[tuple[zipfile.ZipInfo, bytes]] = []
    digest_entries: list[tuple[str, bytes]] = []

    with zipfile.ZipFile(source, "r") as apk:
        for info in apk.infolist():
            if info.filename.upper().startswith("META-INF/"):
                continue
            data = apk.read(info.filename)
            if info.filename == "classes2.dex":
                data = patch_poll_interval(data, interval_ms)

            copied = zipfile.ZipInfo(info.filename, date_time=info.date_time)
            copied.compress_type = info.compress_type
            copied.external_attr = info.external_attr
            copied.comment = info.comment
            unsigned_entries.append((copied, data))
            if not info.is_dir():
                digest_entries.append((info.filename, data))

    manifest, sections = build_manifest(digest_entries)
    sf_bytes = build_signature_file(manifest, sections)
    rsa_bytes = sign_sf(sf_bytes)

    destination.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(destination, "w") as out:
        for name, data in [
            ("META-INF/MANIFEST.MF", manifest),
            ("META-INF/CERT.SF", sf_bytes),
            ("META-INF/CERT.RSA", rsa_bytes),
        ]:
            info = zipfile.ZipInfo(name)
            info.compress_type = zipfile.ZIP_DEFLATED
            out.writestr(info, data)
        for info, data in unsigned_entries:
            out.writestr(info, data)


def main() -> None:
    parser = argparse.ArgumentParser(
        formatter_class=argparse.RawDescriptionHelpFormatter,
        description=textwrap.dedent(__doc__ or ""),
    )
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--destination", required=True, type=Path)
    parser.add_argument("--interval-ms", type=int, default=1000)
    args = parser.parse_args()

    build_fast_apk(args.source, args.destination, args.interval_ms)
    print(args.destination)
    print(hashlib.sha256(args.destination.read_bytes()).hexdigest().upper())


if __name__ == "__main__":
    main()
