import json
from pathlib import Path
import tempfile
import unittest
from mention_hook import poll, next_event, acknowledge, dedup_key


class MentionTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        root = Path(self.tmp.name)
        self.inbox, self.db = root/'inbox.jsonl', root/'mentions.sqlite'
        self.inbox.write_text('')
        poll(self.inbox, self.db)

    def tearDown(self):
        self.tmp.cleanup()

    def append(self, text, ident=1, nick='fixture', direction='in', kind='chat'):
        row = {'dir': direction, 'msg': {'type': kind, 'id': ident, 'nick': nick, 'text': text}}
        with self.inbox.open('a') as f: f.write(json.dumps(row)+'\n')

    def test_exact_mentions_case_and_punctuation(self):
        for i, text in enumerate(['@dot help', 'Hello, @DoT!', 'can @dot check?', '@dot.']): self.append(text, i)
        self.assertEqual(poll(self.inbox, self.db)['queued'], 4)

    def test_ignores_nonmentions_self_outbound_and_other_frames(self):
        for i,text in enumerate(['dot help', '@dotty', 'name@dot.com', '@dot.example', '@dot-bot', 'email@dot']): self.append(text,i)
        self.append('@dot test',20,nick='DOT'); self.append('@dot test',21,direction='out'); self.append('@dot test',22,kind='presence')
        self.assertEqual(poll(self.inbox, self.db)['queued'],0)

    def test_duplicate_and_ack_contract(self):
        self.append('@dot synthetic local test')
        self.assertEqual(poll(self.inbox,self.db)['queued'],1)
        event=next_event(self.db)
        self.assertTrue(event['untrusted'])
        self.assertEqual(next_event(self.db),event)  # Retained until acknowledged.
        self.append('@dot synthetic local test')
        self.assertEqual(poll(self.inbox,self.db)['queued'],0)
        self.assertEqual(acknowledge(self.db,event['event_id']),1)
        self.assertIsNone(next_event(self.db))

    def handoff(self, text, delivery_id, nick='fixture', message_id=None, ts=None):
        msg = {'type': 'chat', 'id': 'v2:' + delivery_id, 'nick': nick, 'text': text}
        if message_id is not None:
            msg['messageId'] = message_id
        if ts is not None:
            msg['ts'] = ts
        row = {'dir': 'in', 'v2_handoff': delivery_id, 'msg': msg}
        with self.inbox.open('a') as f:
            f.write(json.dumps(row) + '\n')

    def test_v2_handoff_keeps_v1_selection(self):
        participants = ['Fixture', 'Other']
        self.append('@dot help', 1, nick='Fixture', kind='delivery')
        self.handoff('This is working now', 'plain', nick='Fixture')
        self.handoff('@Other can you check?', 'other-at', nick='Fixture')
        self.handoff('Other, can you check?', 'other-name', nick='Fixture')
        self.handoff('Does anyone know why this failed?', 'question', nick='Fixture')
        self.handoff('@dot help', 'mention', nick='Fixture')
        self.assertEqual(poll(self.inbox, self.db, participants)['queued'], 2)
        reasons = []
        for _ in range(2):
            event = next_event(self.db)
            reasons.append(event['reason'])
            self.assertEqual(acknowledge(self.db, event['event_id']), 1)
        self.assertEqual(sorted(reasons), ['addressed_to_dot', 'open_question_candidate'])
        self.assertIsNone(next_event(self.db))

    def test_v1_chat_and_v2_handoff_share_a_dedup_key(self):
        v1 = {'type': 'chat', 'id': 'room-msg-1', 'nick': 'fixture', 'trip': 'Zz99Yy', 'text': '@dot status?', 'ts': 1710000000}
        handoff = {'type': 'chat', 'id': 'v2:lease-9', 'messageId': 'room-msg-1', 'nick': 'fixture', 'trip': 'Zz99Yy', 'text': '@dot status?', 'ts': 1710000000}
        bridged = {'type': 'chat', 'id': 'room-msg-1', 'messageId': 'room-msg-1', 'nick': 'fixture', 'trip': 'Zz99Yy', 'text': '@dot status?', 'ts': 1710000000}
        self.assertEqual(dedup_key(v1), dedup_key(handoff))
        self.assertEqual(dedup_key(v1), dedup_key(bridged))
        bare_v1 = {'type': 'chat', 'nick': 'fixture', 'trip': 'Zz99Yy', 'text': '@dot status?', 'ts': 1710000001}
        bare_handoff = {'type': 'chat', 'id': 'v2:lease-10', 'nick': 'fixture', 'trip': 'Zz99Yy', 'text': '@dot status?', 'ts': 1710000001}
        self.assertEqual(dedup_key(bare_v1), dedup_key(bare_handoff))
        with self.inbox.open('a') as f:
            f.write(json.dumps({'dir': 'in', 'msg': v1}) + '\n')
            f.write(json.dumps({'dir': 'in', 'v2_handoff': 'lease-9', 'msg': handoff}) + '\n')
        self.assertEqual(poll(self.inbox, self.db)['queued'], 1)
        event = next_event(self.db)
        self.assertEqual(event['reason'], 'addressed_to_dot')
        self.assertEqual(event['message']['text'], '@dot status?')
        self.assertEqual(acknowledge(self.db, event['event_id']), 1)
        self.assertIsNone(next_event(self.db))
        self.assertEqual(poll(self.inbox, self.db)['queued'], 0)

    def test_replay_is_not_a_new_mention(self):
        message={'type':'chat','id':10,'nick':'fixture','text':'@dot historical'}
        self.inbox.write_text(json.dumps({'dir':'in','msg':{'type':'welcome','replay':[message]}})+'\n'+json.dumps({'dir':'in','msg':message})+'\n')
        self.assertEqual(poll(self.inbox,self.db)['queued'],0)

    def test_partial_line_waits_and_repoll_is_silent(self):
        text=json.dumps({'dir':'in','msg':{'cmd':'chat','id':7,'nick':'fixture','text':'@dot partial'}})
        self.inbox.write_text(text)
        self.assertEqual(poll(self.inbox,self.db)['queued'],0)
        with self.inbox.open('a') as f:f.write('\n')
        self.assertEqual(poll(self.inbox,self.db)['queued'],1)
        self.assertEqual(poll(self.inbox,self.db)['queued'],0)

    def test_first_run_skips_history_and_rotation_deduplicates(self):
        root=Path(self.tmp.name); other=root/'fresh.sqlite'
        self.append('@dot old',1)
        self.assertEqual(poll(self.inbox,other)['queued'],0)
        self.append('@dot new',2)
        self.assertEqual(poll(self.inbox,other)['queued'],1)
        self.inbox.rename(root/'rotated')
        self.inbox.write_text('')
        self.append('@dot old',1); self.append('@dot new',2); self.append('@dot newest',3)
        self.assertEqual(poll(self.inbox,other)['queued'],1)


if __name__=='__main__':unittest.main()
