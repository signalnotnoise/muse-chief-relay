"""Offline MCP Events regressions: temporary state, mocked DNS and callbacks only."""
import base64
import copy
import hashlib
import http.client
import json
import os
from pathlib import Path
import socket
import sqlite3
import tempfile
import threading
import unittest
from unittest import mock

import mcp_events
from mcp_events import Events, ProtocolError, canonical


def make_secret(value=b'a'):
    return 'whsec_' + base64.b64encode(value * 32).decode()


def echo_challenge(url, body, headers):
    return 200, canonical({'challenge': json.loads(body)['challenge']}).encode()


class SimulatedCrash(BaseException):
    pass


class McpEventsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix='dot-mcp-events-')
        self.addCleanup(self.tmp.cleanup)
        self.bot = Path(self.tmp.name)
        self.runtime = self.bot / 'runtime'
        self.runtime.mkdir()
        (self.runtime / 'unread.jsonl').symlink_to('/dev/null')
        (self.runtime / 'outbox.jsonl').write_text('')
        self.config = {
            'url': 'wss://relay.example.test', 'channel': 'fixture-room', 'nick': 'dot',
            'auto_ack': {'enabled': False}, 'base': 'runtime', 'dot_mode': 'participate',
            'durable_outbox': True, 'approved_recipients': ['Alex'],
            'mcp_events': {'principal': 'local-owner', 'auth_token_env': 'DOT_MCP_GATEWAY_TOKEN'},
        }
        self.write_config()
        patch = mock.patch.dict(os.environ, {'DOT_MCP_GATEWAY_TOKEN': 'x' * 40})
        patch.start()
        self.addCleanup(patch.stop)
        # Preserve real callback validation while ensuring no DNS leaves this test.
        dns = mock.patch.object(socket, 'getaddrinfo', return_value=[
            (socket.AF_INET, socket.SOCK_STREAM, 6, '', ('8.8.8.8', 443))])
        self.dns = dns.start()
        self.addCleanup(dns.stop)
        self.room_key = hashlib.sha256(self.config['channel'].encode()).hexdigest()
        self.now = 1_700_000_000.0
        self.services = []
        self.addCleanup(lambda: [svc.close() for svc in self.services])

    def write_config(self):
        (self.bot / 'config.json').write_text(json.dumps(self.config))

    def service(self, sender=echo_challenge):
        svc = Events(self.bot / 'config.json', sender=sender, clock=lambda: self.now)
        self.services.append(svc)
        return svc

    def restart(self, svc, sender=echo_challenge):
        svc.close()
        self.services.remove(svc)
        return self.service(sender)

    def params(self, delivery=None):
        return {'name': mcp_events.NAME,
                'arguments': {'room_key': self.room_key, 'mention_only': True},
                'delivery': self.delivery() if delivery is None else delivery}

    def delivery(self, url='https://callback.example.test/hook', secret=None):
        return {'mode': 'webhook', 'url': url, 'secret': secret or make_secret()}

    def subscribe(self, svc, params=None):
        return svc.rpc({'method': 'events/subscribe', 'params': params or self.params()})['id']

    def seed_event(self, svc, ident='event-1', nick='Alex', reason='addressed_to_dot', created=None):
        payload = {'event_id': ident, 'reason': reason,
                   'message': {'nick': nick, 'text': '@dot fixture', 'ts': self.now}, 'untrusted': True}
        with svc.gate, svc.db:
            svc.db.execute('INSERT INTO events VALUES (?,?,?,0)',
                           (ident, canonical(payload), self.now if created is None else created))
        return ident

    def ready(self):
        svc = self.service()
        sub = self.subscribe(svc)
        ident = self.seed_event(svc)
        return svc, sub, ident

    def row(self, svc, sub, ident='event-1'):
        with svc.gate:
            return svc.db.execute('SELECT state,attempts,next_at,claim,reply,exported FROM deliveries WHERE sub=? AND event=?', (sub, ident)).fetchone()

    def complete(self, svc, ident='event-1', reply='Fixture reply'):
        return svc.rpc({'method': 'tools/call', 'params': {'name': 'dot_complete',
                        'arguments': {'event_id': ident, 'reply': reply}}})

    def status(self, svc):
        return svc.rpc({'method': 'tools/call', 'params': {'name': 'dot_status'}})

    def thread(self, action):
        result, errors = [], []
        def run():
            try:
                result.append(action())
            except BaseException as exc:
                errors.append(exc)
        thread = threading.Thread(target=run, daemon=True)
        thread.start()
        return thread, result, errors

    def finish(self, task):
        thread, result, errors = task
        thread.join(2)
        self.assertFalse(thread.is_alive(), 'service lock blocked another request during callback I/O')
        if errors:
            raise errors[0]
        return result[0]

    def blocked_tick(self, svc, status=200):
        entered, release = threading.Event(), threading.Event()
        self.addCleanup(release.set)
        def sender(*args):
            entered.set()
            if not release.wait(4):
                raise AssertionError('callback was not released')
            return status, b''
        svc.sender = sender
        task = self.thread(svc.tick)
        self.assertTrue(entered.wait(2))
        return release, task

    def test_malformed_delivery_is_rejected(self):
        svc = self.service()
        for value in (None, [], 'callback', 7):
            params = self.params()
            params['delivery'] = value
            with self.subTest(value=value), self.assertRaisesRegex(ProtocolError, 'invalid_or_unauthorized_filters'):
                svc.subscribe(params)

    def test_challenge_non_object_or_wrong_value_fails(self):
        svc = self.service()
        for body in (b'[]', b'null', b'42', b'"text"', b'{"challenge":"wrong"}'):
            svc.sender = lambda *args: (200, body)
            with self.subTest(body=body), self.assertRaisesRegex(ProtocolError, 'challenge_failed'):
                svc.subscribe(self.params())
            self.assertEqual(svc.db.execute('SELECT count(*) FROM subscriptions').fetchone()[0], 0)
            self.assertEqual(svc.db.execute('SELECT count(*) FROM subscription_checks').fetchone()[0], 0)

    def test_bad_challenge_transport_fails_cleanly(self):
        svc = self.service()
        for error in (OSError(), http.client.HTTPException()):
            svc.sender = mock.Mock(side_effect=error)
            with self.assertRaisesRegex(ProtocolError, 'callback_failed'):
                svc.subscribe(self.params())
        svc.sender = lambda *args: (200, b'not-json')
        with self.assertRaisesRegex(ProtocolError, 'callback_failed'):
            svc.subscribe(self.params())

    def test_subscribe_happy_path_and_ttl(self):
        svc = self.service()
        params = self.params()
        params['ttlMs'] = 200_000_000
        sub = self.subscribe(svc, params)
        row = svc.db.execute('SELECT owner,expires,starts,active FROM subscriptions WHERE id=?', (sub,)).fetchone()
        self.assertEqual(row, ('local-owner', self.now + 86400, self.now, 1))
        self.assertEqual(self.subscribe(svc, params), sub)
        self.assertEqual(svc.db.execute('SELECT count(*) FROM subscriptions').fetchone()[0], 1)

    def test_bad_ttl_or_scope_is_rejected_before_callback(self):
        sender = mock.Mock()
        svc = self.service(sender)
        for value in (0, -1, True, '300', float('inf'), float('nan')):
            params = self.params()
            params['ttlMs'] = value
            with self.assertRaisesRegex(ProtocolError, 'invalid_lifetime'):
                svc.subscribe(params)
        params = self.params()
        params['arguments']['room_key'] = 'wrong-room'
        with self.assertRaisesRegex(ProtocolError, 'invalid_or_unauthorized_filters'):
            svc.subscribe(params)
        sender.assert_not_called()

    def test_mixed_case_config_refused_from_valid_fixture(self):
        svc = self.service()
        svc.config()
        self.config['NICK'] = 'dot'
        self.write_config()
        with self.assertRaisesRegex(ValueError, 'config keys must be lowercase and unique'):
            svc.config()
        with self.assertRaisesRegex(ValueError, 'config keys must be lowercase and unique'):
            self.service()

    def test_verification_can_synchronously_call_status_on_another_thread(self):
        svc = self.service()
        def sender(*args):
            self.finish(self.thread(lambda: self.status(svc)))
            return echo_challenge(*args)
        svc.sender = sender
        self.subscribe(svc)

    def test_dns_validation_is_outside_rpc_lock(self):
        svc = self.service()
        original = self.dns.return_value
        def resolve(*args, **kwargs):
            self.finish(self.thread(lambda: self.status(svc)))
            return original
        self.dns.side_effect = resolve
        self.subscribe(svc)

    def test_callback_can_synchronously_complete_on_another_thread(self):
        svc, sub, ident = self.ready()
        for status in (200, 500, 410):
            # Each iteration is a different event but the same verified subscription.
            if status != 200:
                ident = self.seed_event(svc, ident='event-' + str(status))
            def sender(*args):
                self.finish(self.thread(lambda: self.complete(svc, ident)))
                return status, b''
            svc.sender = sender
            self.assertEqual(svc.tick(), 'completed')
            self.assertEqual(self.row(svc, sub, ident)[0], 'completed')
            self.assertEqual(svc.db.execute('SELECT active FROM subscriptions WHERE id=?', (sub,)).fetchone()[0], 1)

    def test_concurrent_tick_does_not_claim_same_delivery_twice(self):
        svc, sub, _ = self.ready()
        release, task = self.blocked_tick(svc)
        self.assertEqual(self.finish(self.thread(svc.tick)), 'idle')
        self.finish(self.thread(lambda: self.status(svc)))
        self.assertEqual(self.row(svc, sub)[:2], ('delivering', 1))
        release.set()
        self.assertEqual(self.finish(task), 'accepted')

    def test_claim_is_committed_before_callback_and_stale_result_is_ignored(self):
        svc, sub, _ = self.ready()
        def sender(*args):
            with sqlite3.connect(svc.dbpath) as observer:
                row = observer.execute('SELECT state,attempts,claim FROM deliveries').fetchone()
            self.assertEqual(row[:2], ('delivering', 1))
            self.assertIsNotNone(row[2])
            # Simulate a superseding claim to verify exact attempt correlation.
            with svc.gate, svc.db:
                svc.db.execute("UPDATE deliveries SET claim='newer-attempt',attempts=2")
            return 410, b''
        svc.sender = sender
        self.assertEqual(svc.tick(), 'delivering')
        self.assertEqual(self.row(svc, sub)[3], 'newer-attempt')
        self.assertEqual(svc.db.execute('SELECT active FROM subscriptions').fetchone()[0], 1)

    def test_invalid_reload_during_send_releases_claim_for_bounded_recovery(self):
        svc, sub, _ = self.ready()
        def sender(*args):
            self.config['NICK'] = 'dot'
            self.write_config()
            return 200, b''
        svc.sender = sender
        with self.assertRaisesRegex(ValueError, 'config keys must be lowercase and unique'):
            svc.tick()
        self.assertEqual(self.row(svc, sub)[:2], ('pending', 1))
        self.assertIsNone(self.row(svc, sub)[3])
        del self.config['NICK']
        self.write_config()
        self.now = self.row(svc, sub)[2]
        svc.sender = lambda *args: (200, b'')
        self.assertEqual(svc.tick(), 'accepted')

    def test_structurally_invalid_reload_does_not_strand_claim_after_repair(self):
        svc, sub, _ = self.ready()
        invalid = [[], dict(self.config, channel=None), dict(self.config, mcp_events=None),
                   dict(self.config, mcp_events={'principal': 'local-owner', 'auth_token_env': []})]
        for index, config in enumerate(invalid):
            ident = 'event-1' if index == 0 else self.seed_event(svc, 'event-' + str(index + 1))
            with self.subTest(config=config):
                def sender(*args):
                    (self.bot / 'config.json').write_text(json.dumps(config))
                    return 200, b''
                svc.sender = sender
                with self.assertRaisesRegex(ValueError, 'invalid_configuration_structure'):
                    svc.tick()
                row = self.row(svc, sub, ident)
                self.assertEqual(row[:2], ('pending', 1))
                self.assertIsNone(row[3])
                self.write_config()
                self.now = row[2]
                svc.sender = lambda *args: (200, b'')
                self.assertEqual(svc.tick(), 'accepted')

    def test_unsubscribe_during_send_prevents_stale_acceptance_and_completion(self):
        svc, sub, _ = self.ready()
        release, task = self.blocked_tick(svc)
        self.finish(self.thread(lambda: svc.rpc({'method': 'events/unsubscribe', 'params': self.params()})))
        release.set()
        self.assertEqual(self.finish(task), 'cancelled')
        self.assertIsNone(self.row(svc, sub)[3])
        with self.assertRaisesRegex(ProtocolError, 'event_not_authorized'):
            self.complete(svc)

    def test_expiry_during_send_cannot_be_overwritten(self):
        svc, sub, _ = self.ready()
        release, task = self.blocked_tick(svc)
        self.now += 86400
        release.set()
        self.assertEqual(self.finish(task), 'expired')
        with self.assertRaisesRegex(ProtocolError, 'event_not_authorized'):
            self.complete(svc)
        self.assertEqual(self.row(svc, sub)[0], 'expired')

    def test_owner_room_or_recipient_change_revokes_inflight_delivery(self):
        for field in ('principal', 'channel', 'approved_recipients'):
            with self.subTest(field=field):
                # Separate fixture state for each scenario.
                svc, sub, _ = self.ready()
                release, task = self.blocked_tick(svc)
                prior = copy.deepcopy(self.config)
                if field == 'principal':
                    self.config['mcp_events']['principal'] = 'other-owner'
                else:
                    self.config[field] = 'other-room' if field == 'channel' else ['SomeoneElse']
                self.write_config()
                release.set()
                self.assertEqual(self.finish(task), 'revoked')
                with self.assertRaises(ProtocolError):
                    self.complete(svc)
                self.config = prior
                self.write_config()
                # Ready's stable fixture event should be independent next iteration.
                svc.close()
                self.services.remove(svc)
                (self.runtime / 'mcp-events.sqlite').unlink()

    def test_unsubscribe_during_verification_does_not_reactivate(self):
        svc = self.service()
        def sender(*args):
            self.finish(self.thread(lambda: svc.unsubscribe(self.params())))
            return echo_challenge(*args)
        svc.sender = sender
        with self.assertRaisesRegex(ProtocolError, 'subscription_superseded'):
            self.subscribe(svc)
        self.assertEqual(svc.db.execute('SELECT count(*) FROM subscriptions WHERE active=1').fetchone()[0], 0)

    def test_newer_subscription_verification_wins(self):
        svc = self.service()
        newer = self.params(self.delivery(secret=make_secret(b'b')))
        first = True
        def sender(*args):
            nonlocal first
            if first:
                first = False
                self.finish(self.thread(lambda: self.subscribe(svc, newer)))
            return echo_challenge(*args)
        svc.sender = sender
        with self.assertRaisesRegex(ProtocolError, 'subscription_superseded'):
            self.subscribe(svc)
        self.assertEqual(svc.db.execute('SELECT secret FROM subscriptions').fetchone()[0], newer['delivery']['secret'])

    def test_verification_that_outlives_granted_ttl_fails(self):
        svc = self.service()
        def sender(*args):
            self.now += 1
            return echo_challenge(*args)
        svc.sender = sender
        params = self.params()
        params['ttlMs'] = 1000
        with self.assertRaisesRegex(ProtocolError, 'subscription_expired'):
            self.subscribe(svc, params)

    def test_verification_cannot_activate_after_owner_change(self):
        svc = self.service()
        def sender(*args):
            self.config['mcp_events']['principal'] = 'other-owner'
            self.write_config()
            return echo_challenge(*args)
        svc.sender = sender
        with self.assertRaisesRegex(ProtocolError, 'subscription_not_authorized'):
            self.subscribe(svc)
        self.assertEqual(svc.db.execute('SELECT count(*) FROM subscriptions').fetchone()[0], 0)

    def test_stale_410_cannot_disable_refreshed_subscription(self):
        svc, sub, _ = self.ready()
        def sender(url, body, headers):
            if json.loads(body).get('type') == 'verification':
                return echo_challenge(url, body, headers)
            self.finish(self.thread(lambda: self.subscribe(svc)))
            return 410, b''
        svc.sender = sender
        self.assertEqual(svc.tick(), 'rejected')
        self.assertEqual(svc.db.execute('SELECT active FROM subscriptions WHERE id=?', (sub,)).fetchone()[0], 1)

    def test_410_disables_subscription_and_cancels_other_pending_deliveries(self):
        svc, sub, _ = self.ready()
        self.seed_event(svc, 'event-2')
        svc.sender = lambda *args: (410, b'')
        self.assertEqual(svc.tick(), 'rejected')
        self.assertEqual(self.row(svc, sub, 'event-2')[0], 'cancelled')
        self.assertEqual(svc.tick(), 'idle')

    def test_bounded_retry_backoff_and_stable_webhook_id(self):
        svc, sub, _ = self.ready()
        calls = []
        def sender(url, body, headers):
            calls.append((body, headers))
            return 503, b''
        svc.sender = sender
        for attempt in range(1, mcp_events.MAX_ATTEMPTS + 1):
            expected = 'pending' if attempt < mcp_events.MAX_ATTEMPTS else 'exhausted'
            self.assertEqual(svc.tick(), expected)
            state, count, next_at, claim, _, _ = self.row(svc, sub)
            self.assertEqual((state, count, claim), (expected, attempt, None))
            self.assertEqual(next_at, self.now + mcp_events.retry_delay(attempt))
            self.assertEqual(svc.tick(), 'idle')
            self.now = next_at
        self.assertEqual(len(calls), 8)
        self.assertEqual({headers['webhook-id'] for _, headers in calls}, {'evt_event-1'})
        self.assertEqual(len({body for body, _ in calls}), 1)

    def test_callback_exception_retries_and_413_is_terminal(self):
        svc, sub, _ = self.ready()
        svc.sender = mock.Mock(side_effect=OSError('fixture disconnect'))
        self.assertEqual(svc.tick(), 'pending')
        self.now = self.row(svc, sub)[2]
        svc.sender = mock.Mock(return_value=(413, b''))
        self.assertEqual(svc.tick(), 'rejected')
        self.assertEqual(svc.tick(), 'idle')
        self.assertEqual(svc.sender.call_count, 1)

    def test_oversize_event_is_rejected_without_http(self):
        svc, sub, _ = self.ready()
        with svc.db:
            raw = json.loads(svc.db.execute('SELECT payload FROM events').fetchone()[0])
            raw['message']['text'] = 'a' * 262144
            svc.db.execute('UPDATE events SET payload=?', (canonical(raw),))
        svc.sender = mock.Mock()
        self.assertEqual(svc.tick(), 'rejected')
        svc.sender.assert_not_called()

    def test_repeated_crashes_consume_retry_budget_and_preserve_backoff(self):
        svc, sub, _ = self.ready()
        for attempt in range(1, mcp_events.MAX_ATTEMPTS + 1):
            svc.sender = mock.Mock(side_effect=SimulatedCrash())
            with self.assertRaises(SimulatedCrash):
                svc.tick()
            before = self.row(svc, sub)
            self.assertEqual(before[:2], ('delivering', attempt))
            self.assertIsNotNone(before[3])
            svc = self.restart(svc)
            after = self.row(svc, sub)
            self.assertEqual(after[:3], ('pending' if attempt < 8 else 'exhausted', attempt, before[2]))
            self.assertIsNone(after[3])
            self.assertEqual(svc.tick(), 'idle')
            self.now = before[2]
        self.assertEqual(svc.tick(), 'idle')

    def test_completion_is_idempotent_and_conflicting_reply_is_rejected(self):
        svc, sub, _ = self.ready()
        svc.sender = lambda *args: (200, b'')
        self.assertEqual(svc.tick(), 'accepted')
        self.complete(svc)
        paths = list((self.runtime / 'durable-outbox').glob('*.json'))
        self.assertEqual(len(paths), 1)
        before = paths[0].stat().st_ino
        self.complete(svc)
        svc.export()
        self.assertEqual(paths[0].stat().st_ino, before)
        with self.assertRaisesRegex(ProtocolError, 'completion_conflict'):
            self.complete(svc, reply='Different reply')
        self.assertEqual(self.row(svc, sub)[4:], ('Fixture reply', 1))
        self.assertEqual(svc.db.execute('SELECT delivered FROM events').fetchone()[0], 1)

    def test_export_recovers_crash_after_publish_without_duplicate_request(self):
        svc, sub, _ = self.ready()
        svc.sender = lambda *args: (200, b'')
        svc.tick()
        real_publish = mcp_events.publish
        def crash_after_publish(*args):
            real_publish(*args)
            raise SimulatedCrash()
        with mock.patch.object(mcp_events, 'publish', side_effect=crash_after_publish):
            with self.assertRaises(SimulatedCrash):
                self.complete(svc)
        self.assertEqual(self.row(svc, sub)[0::5], ('completed', 0))
        path = next((self.runtime / 'durable-outbox').glob('*.json'))
        inode = path.stat().st_ino
        svc = self.restart(svc)
        self.assertEqual(svc.tick(), 'idle')
        self.assertEqual(self.row(svc, sub)[5], 1)
        self.assertEqual(path.stat().st_ino, inode)
        self.assertEqual(len(list(path.parent.glob('*.json'))), 1)

    def test_export_rechecks_owner_room_and_recipient(self):
        svc, sub, _ = self.ready()
        svc.sender = lambda *args: (200, b'')
        svc.tick()
        with mock.patch.object(mcp_events, 'publish', side_effect=OSError('fixture disk full')):
            with self.assertRaises(OSError):
                self.complete(svc)
        original = copy.deepcopy(self.config)
        for field in ('principal', 'channel', 'approved_recipients'):
            self.config = copy.deepcopy(original)
            if field == 'principal':
                self.config['mcp_events']['principal'] = 'other-owner'
            else:
                self.config[field] = 'other-room' if field == 'channel' else ['SomeoneElse']
            self.write_config()
            with mock.patch.object(mcp_events, 'publish') as publish:
                svc.export()
                publish.assert_not_called()
            self.assertEqual(self.row(svc, sub)[5], 0)
        self.config = original
        self.write_config()
        svc.export()
        self.assertEqual(self.row(svc, sub)[5], 1)

    def test_completion_does_not_overwrite_cancelled_other_subscription(self):
        svc, sub, _ = self.ready()
        params2 = self.params(self.delivery('https://other.example.test/hook'))
        sub2 = self.subscribe(svc, params2)
        svc.sender = lambda *args: (200, b'')
        svc.tick()
        svc.tick()
        svc.unsubscribe(params2)
        self.complete(svc)
        self.assertEqual(self.row(svc, sub)[0], 'completed')
        self.assertEqual(self.row(svc, sub2)[0], 'cancelled')

    def test_completion_without_reply_survives_restart(self):
        svc, sub, _ = self.ready()
        svc.sender = lambda *args: (200, b'')
        svc.tick()
        self.complete(svc, reply=None)
        svc = self.restart(svc)
        self.complete(svc, reply=None)
        self.assertEqual(self.row(svc, sub)[0], 'completed')
        self.assertFalse((self.runtime / 'durable-outbox').exists())
        with self.assertRaisesRegex(ProtocolError, 'completion_conflict'):
            self.complete(svc)

    def test_expired_or_unsubscribed_subscription_does_not_replay_old_deliveries(self):
        svc, sub, _ = self.ready()
        svc.sender = lambda *args: (200, b'')
        svc.tick()
        self.now += 86400
        svc.tick()
        svc.sender = echo_challenge
        self.subscribe(svc)
        self.assertEqual(self.row(svc, sub)[0], 'expired')
        svc.sender = mock.Mock()
        self.assertEqual(svc.tick(), 'idle')
        svc.sender.assert_not_called()

    def test_filters_and_start_time_prevent_unapproved_or_historical_delivery(self):
        svc = self.service()
        self.seed_event(svc, 'history', created=self.now - 1)
        self.subscribe(svc)
        self.seed_event(svc, 'unapproved', nick='Other')
        self.seed_event(svc, 'open', reason='open_question_candidate')
        svc.sender = mock.Mock()
        self.assertEqual(svc.tick(), 'idle')
        svc.sender.assert_not_called()

    def test_rotation_dual_signs_for_only_the_grace_window(self):
        svc, sub, _ = self.ready()
        self.now += 1
        self.subscribe(svc, self.params(self.delivery(secret=make_secret(b'b'))))
        calls = []
        svc.sender = lambda url, body, headers: (calls.append(headers) or (200, b''))
        svc.tick()
        self.assertEqual(len(calls[0]['webhook-signature'].split()), 2)
        self.now += 301
        self.seed_event(svc, 'event-2')
        svc.tick()
        self.assertEqual(len(calls[1]['webhook-signature'].split()), 1)

    def test_old_schema_migrates_without_losing_completed_reply(self):
        svc, sub, _ = self.ready()
        svc.sender = lambda *args: (200, b'')
        svc.tick()
        self.complete(svc)
        # Recreate the two original table schemas to exercise additive migration.
        with svc.db:
            svc.db.execute('ALTER TABLE deliveries DROP COLUMN claim')
            svc.db.execute('ALTER TABLE subscriptions DROP COLUMN generation')
            svc.db.execute('DROP TABLE subscription_checks')
        svc = self.restart(svc)
        self.assertEqual(self.row(svc, sub)[0], 'completed')
        self.assertEqual(self.row(svc, sub)[4:], ('Fixture reply', 1))


class CallbackAddressTests(unittest.TestCase):
    def test_only_public_https_without_credentials_or_fragment(self):
        public = [(socket.AF_INET, socket.SOCK_STREAM, 6, '', ('8.8.8.8', 443))]
        with mock.patch.object(socket, 'getaddrinfo', return_value=public):
            for url in ('http://callback.example.test', 'https://user:pass@callback.example.test',
                        'https://callback.example.test/#fragment'):
                with self.subTest(url=url), self.assertRaisesRegex(ProtocolError, 'invalid_callback'):
                    mcp_events.callback_target(url)
            target, addresses = mcp_events.callback_target('https://callback.example.test/hook?q=1')
            self.assertEqual(target.path, '/hook')
            self.assertEqual(addresses, public)

    def test_private_or_mixed_dns_and_resolution_failure_are_rejected(self):
        for ips in (['127.0.0.1'], ['10.0.0.1'], ['::1'], ['169.254.169.254'], ['8.8.8.8', '192.168.1.1']):
            addresses = [(socket.AF_INET, socket.SOCK_STREAM, 6, '', (ip, 443)) for ip in ips]
            with mock.patch.object(socket, 'getaddrinfo', return_value=addresses):
                with self.subTest(ips=ips), self.assertRaisesRegex(ProtocolError, 'non_public_callback'):
                    mcp_events.callback_target('https://callback.example.test/hook')
        with mock.patch.object(socket, 'getaddrinfo', side_effect=socket.gaierror()):
            with self.assertRaisesRegex(ProtocolError, 'callback_resolution_failed'):
                mcp_events.callback_target('https://callback.example.test/hook')

    def test_post_revalidates_dns_each_time_and_never_follows_redirect(self):
        responses = [(socket.AF_INET, socket.SOCK_STREAM, 6, '', ('8.8.8.8', 443))]
        response = mock.Mock(status=302)
        response.read.return_value = b''
        with mock.patch.object(socket, 'getaddrinfo', return_value=responses) as dns, \
                mock.patch.object(http.client.HTTPSConnection, 'request') as request, \
                mock.patch.object(http.client.HTTPSConnection, 'getresponse', return_value=response):
            for _ in range(2):
                self.assertEqual(mcp_events.post_callback('https://callback.example.test/hook?q=1', b'{}', {})[0], 302)
            self.assertEqual(dns.call_count, 2)
            self.assertEqual(request.call_count, 2)
            self.assertEqual(request.call_args.args[:2], ('POST', '/hook?q=1'))

    def test_connection_pins_validated_address_and_tls_hostname(self):
        address = (socket.AF_INET, socket.SOCK_STREAM, 6, '', ('8.8.8.8', 443))
        raw, context = mock.Mock(), mock.Mock()
        response = mock.Mock(status=204)
        response.read.return_value = b''
        def request(connection, *args, **kwargs):
            connection.connect()
        with mock.patch.object(socket, 'getaddrinfo', return_value=[address]), \
                mock.patch.object(socket, 'socket', return_value=raw), \
                mock.patch.object(mcp_events.ssl, 'create_default_context', return_value=context), \
                mock.patch.object(http.client.HTTPSConnection, 'request', new=request), \
                mock.patch.object(http.client.HTTPSConnection, 'getresponse', return_value=response):
            mcp_events.post_callback('https://callback.example.test/hook', b'{}', {})
        raw.connect.assert_called_once_with(('8.8.8.8', 443))
        context.wrap_socket.assert_called_once_with(raw, server_hostname='callback.example.test')


if __name__ == '__main__':
    unittest.main()
