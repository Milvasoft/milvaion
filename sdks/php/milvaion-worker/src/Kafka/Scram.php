<?php

declare(strict_types=1);

namespace Milvasoft\Milvaion\Worker\Kafka;

/**
 * Client side of a SASL SCRAM exchange (RFC 5802, RFC 7677), used by Admin to authenticate.
 *
 * @internal
 */
final class Scram
{
    private readonly string $nonce;
    private string $authMessage = '';
    private string $saltedPassword = '';

    /** @param string $algorithm "sha256" or "sha512" */
    public function __construct(
        private readonly string $algorithm,
        private readonly string $username,
        private readonly string $password,
        ?string $nonce = null,
    ) {
        $this->nonce = $nonce ?? rtrim(strtr(base64_encode(random_bytes(24)), '+/', '-_'), '=');
    }

    public function clientFirst(): string
    {
        return 'n,,' . $this->clientFirstBare();
    }

    /** The client-final message for the server-first message. */
    public function clientFinal(string $serverFirst): string
    {
        $fields = self::fields($serverFirst);
        $nonce = $fields['r'] ?? '';
        $salt = base64_decode($fields['s'] ?? '', true);
        $iterations = (int) ($fields['i'] ?? 0);

        if (!str_starts_with($nonce, $this->nonce) || $salt === false || $iterations < 1) {
            throw new \RuntimeException('Invalid SCRAM server-first message.');
        }

        $withoutProof = 'c=biws,r=' . $nonce;
        $this->authMessage = $this->clientFirstBare() . ',' . $serverFirst . ',' . $withoutProof;
        $this->saltedPassword = hash_pbkdf2($this->algorithm, $this->password, $salt, $iterations, 0, true);
        $clientKey = hash_hmac($this->algorithm, 'Client Key', $this->saltedPassword, true);
        $signature = hash_hmac($this->algorithm, $this->authMessage, hash($this->algorithm, $clientKey, true), true);

        return $withoutProof . ',p=' . base64_encode($clientKey ^ $signature);
    }

    /** Checks the server's signature in the server-final message. */
    public function verifyServerFinal(string $serverFinal): void
    {
        $fields = self::fields($serverFinal);

        if (isset($fields['e'])) {
            throw new \RuntimeException("SCRAM authentication failed: {$fields['e']}");
        }

        $serverKey = hash_hmac($this->algorithm, 'Server Key', $this->saltedPassword, true);
        $expected = base64_encode(hash_hmac($this->algorithm, $this->authMessage, $serverKey, true));

        if (!hash_equals($expected, $fields['v'] ?? '')) {
            throw new \RuntimeException('SCRAM server signature does not match.');
        }
    }

    private function clientFirstBare(): string
    {
        return 'n=' . str_replace(['=', ','], ['=3D', '=2C'], $this->username) . ',r=' . $this->nonce;
    }

    /** @return array<string, string> */
    private static function fields(string $message): array
    {
        $fields = [];

        foreach (explode(',', $message) as $part) {
            if (strlen($part) >= 2 && $part[1] === '=') {
                $fields[$part[0]] = substr($part, 2);
            }
        }

        return $fields;
    }
}
